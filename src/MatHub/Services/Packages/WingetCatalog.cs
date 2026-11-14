using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MatHub.Data;
using MatHub.Services.Access;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using YamlDotNet.RepresentationModel;
using PackageVersion = MatHub.Data.PackageVersion;

namespace MatHub.Services.Packages;

/// <summary>When the catalog was loaded and how many programs it has.</summary>
public sealed record CatalogState(DateTime? Loaded, int Programs, string? Error);

/// <summary>A program in the list of the store.</summary>
public sealed record CatalogRow(string WingetId, string Name, string Version, string? Moniker, IReadOnlyList<string> Tags, long? PackageId);

/// <summary>An installer of a program (one per architecture, scope, language and type).</summary>
public sealed record CatalogInstaller(int Index, string? Architecture, string Type, string? Scope, string? Locale, string Url, string? Sha256, string? Silent, string? ProductCode)
{
    /// <summary>MSI and the installers MatHub knows the silent switches of can be kept in MatHub; MSIX, ZIP and portable ones only through winget.</summary>
    public bool Cacheable => WingetCatalog.PackageKindOf(Type) is not null;
}

/// <summary>The details of a program, from its manifest.</summary>
public sealed record CatalogDetail(
    string WingetId, string Name, string Version, string? Publisher, string? ShortDescription, string? Description, string? License, string? Homepage,
    string? PublisherUrl, string? IconSha256, string? DisplayName, IReadOnlyList<string> Tags, IReadOnlyList<CatalogInstaller> Installers, long? PackageId);

/// <summary>
/// The store of MatHub: the catalog of the Windows Package Manager (winget). Every day MatHub loads the index of the source of Microsoft
/// (<c>cdn.winget.microsoft.com/cache/source.msix</c>, a signed package with an SQLite database) and keeps the latest version of every program in its own table,
/// so the search is fast and works without the internet. The details come from the manifest of the program when it is opened (kept on disk), its icon too. A
/// program becomes a package with one click: as a winget package (the client installs the latest version from the internet) or with its installer kept in
/// MatHub (downloaded once, checked by the SHA-256 of the manifest; the clients install exactly this version from MatHub, also without winget).
/// </summary>
public sealed class WingetCatalog(IHttpClientFactory http, IConfiguration configuration, PackageFiles packageFiles, ILogger<WingetCatalog> logger)
{
    public const string Cdn = "https://cdn.winget.microsoft.com/cache/";

    private static readonly SemaphoreSlim Loading = new(1, 1);

    public string Root => Path.Combine(configuration["DataDirectory"] ?? "/data", "winget");

    private string StatePath => Path.Combine(Root, "state.json");

    public CatalogState State()
    {
        try
        {
            return File.Exists(StatePath) ? JsonSerializer.Deserialize<CatalogState>(File.ReadAllText(StatePath)) ?? new CatalogState(null, 0, null) : new CatalogState(null, 0, null);
        }
        catch (JsonException)
        {
            return new CatalogState(null, 0, null);
        }
    }

    /// <summary>Loads the catalog when it is older than a day (the housekeeping calls it every hour).</summary>
    public async Task RefreshIfOldAsync(AppDbContext db, CancellationToken ct)
    {
        if (State().Loaded is not { } loaded || DateTime.UtcNow - loaded > TimeSpan.FromHours(24))
        {
            if ((await RefreshAsync(db, ct)).Error is null)
            {
                await PrepareNewerAsync(db, ct);
            }
        }
    }

    /// <summary>Loads the index of winget and puts the latest version of every program into the table <c>CatalogPackage</c>.</summary>
    public async Task<CatalogState> RefreshAsync(AppDbContext db, CancellationToken ct)
    {
        await Loading.WaitAsync(ct);
        var temp = Path.Combine(Path.GetTempPath(), "mathub-winget-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(temp);
            var msix = Path.Combine(temp, "source.msix");
            using (var client = http.CreateClient("winget"))
            await using (var response = await client.GetStreamAsync(Cdn + "source.msix", ct))
            await using (var file = File.Create(msix))
            {
                await response.CopyToAsync(file, ct);
            }

            var index = Path.Combine(temp, "index.db");
            using (var zip = ZipFile.OpenRead(msix))
            {
                var entry = zip.GetEntry("Public/index.db") ?? throw new InvalidDataException("The source of winget has no index.");
                entry.ExtractToFile(index);
            }

            var programs = Read(index);
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                await db.CatalogPackages.ExecuteDeleteAsync(ct);
                foreach (var chunk in programs.Chunk(2000))
                {
                    db.CatalogPackages.AddRange(chunk);
                    await db.SaveChangesAsync(ct);
                    db.ChangeTracker.Clear();
                }

                await tx.CommitAsync(ct);
            }

            var state = new CatalogState(DateTime.UtcNow, programs.Count, null);
            await File.WriteAllTextAsync(StatePath, JsonSerializer.Serialize(state), ct);
            logger.LogInformation("The winget catalog has {Count} programs.", programs.Count);
            return state;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or SqliteException or TaskCanceledException)
        {
            logger.LogWarning("The winget catalog could not be loaded: {Message}", ex.Message);
            var state = State() with { Error = ex.Message };
            await File.WriteAllTextAsync(StatePath, JsonSerializer.Serialize(state), CancellationToken.None);
            return state;
        }
        finally
        {
            Loading.Release();
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
                // SQLite may hold the file a moment longer
            }
        }
    }

    /// <summary>The latest manifest of every program of the index (version 1 of its schema): name, moniker, version, tags, publisher, the path of the manifest.</summary>
    public static List<CatalogPackage> Read(string index)
    {
        using var connection = new SqliteConnection($"Data Source={index};Mode=ReadOnly;Pooling=False");
        connection.Open();
        var parts = new Dictionary<long, (long? Parent, string Part)>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT rowid, parent, pathpart FROM pathparts";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                parts[r.GetInt64(0)] = (r.IsDBNull(1) ? null : r.GetInt64(1), r.GetString(2));
            }
        }

        var latest = new Dictionary<string, (long Manifest, string Name, string? Moniker, string Version, long Path)>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT m.rowid, i.id, n.name, mo.moniker, v.version, m.pathpart
                FROM manifest m JOIN ids i ON i.rowid = m.id JOIN names n ON n.rowid = m.name JOIN versions v ON v.rowid = m.version
                LEFT JOIN monikers mo ON mo.rowid = m.moniker
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetString(1);
                var version = r.GetString(4);
                if (!latest.TryGetValue(id, out var known) || CompareVersions(version, known.Version) > 0)
                {
                    latest[id] = (r.GetInt64(0), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), version, r.GetInt64(5));
                }
            }
        }

        var chosen = latest.Values.Select(v => v.Manifest).ToHashSet();
        var tags = Map(connection, "SELECT t.manifest, g.tag FROM tags_map t JOIN tags g ON g.rowid = t.tag", chosen);
        var publishers = Map(connection, "SELECT p.manifest, n.norm_publisher FROM norm_publishers_map p JOIN norm_publishers n ON n.rowid = p.norm_publisher", chosen);

        string PathOf(long part)
        {
            var segments = new List<string>();
            long? at = part;
            while (at is { } p && parts.TryGetValue(p, out var node))
            {
                segments.Add(node.Part);
                at = node.Parent;
            }

            segments.Reverse();
            return string.Join('/', segments);
        }

        return latest.Select(e => new CatalogPackage
        {
            WingetId = Cut(e.Key, 200)!,
            Name = Cut(e.Value.Name, 300)!,
            Moniker = Cut(e.Value.Moniker, 100),
            Version = Cut(e.Value.Version, 100)!,
            Publisher = Cut(publishers.GetValueOrDefault(e.Value.Manifest)?.FirstOrDefault(), 200),
            Tags = Cut(string.Join(' ', tags.GetValueOrDefault(e.Value.Manifest) ?? []), 2000) ?? string.Empty,
            ManifestPath = Cut(PathOf(e.Value.Path), 400)!,
        }).ToList();
    }

    private static Dictionary<long, List<string>> Map(SqliteConnection connection, string sql, HashSet<long> chosen)
    {
        var map = new Dictionary<long, List<string>>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var manifest = r.GetInt64(0);
            if (chosen.Contains(manifest))
            {
                (map.TryGetValue(manifest, out var list) ? list : map[manifest] = []).Add(r.GetString(1));
            }
        }

        return map;
    }

    /// <summary>
    /// Versions as winget compares them: part by part at the dots, each part by its leading number ("10.2" after "9.11"); with the same number a part with
    /// something after it comes before the part without ("2.0.0-rc1" before "2.0.0", "1.2.beta" before "1.2.0"), two such rests compare as text. Missing
    /// parts are 0 ("1.0" = "1.0.0").
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        var x = a.Trim().Split('.');
        var y = b.Trim().Split('.');
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var (m, p) = Part(i < x.Length ? x[i] : "0");
            var (n, q) = Part(i < y.Length ? y[i] : "0");
            if (m != n)
            {
                return m.CompareTo(n);
            }

            if (p.Length == 0 != (q.Length == 0))
            {
                return p.Length == 0 ? 1 : -1;
            }

            var c = string.Compare(p, q, StringComparison.OrdinalIgnoreCase);
            if (c != 0)
            {
                return c;
            }
        }

        return 0;

        static (long Number, string Tail) Part(string part)
        {
            var digits = 0;
            while (digits < part.Length && char.IsAsciiDigit(part[digits]))
            {
                digits++;
            }

            var number = digits == 0 ? 0 : long.TryParse(part.AsSpan(0, Math.Min(digits, 18)), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;
            return (number, part[digits..]);
        }
    }

    // ---- the store --------------------------------------------------------------------------------------------------------------

    /// <summary>The programs that fit the search: an exact id or moniker first, then names that begin with it, then the rest; each with its package, if there is one.</summary>
    public static async Task<(IReadOnlyList<CatalogRow> Items, int Matching)?> SearchAsync(AppDbContext db, Actor actor, string? search, int page, int pageSize, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.PackagesView))
        {
            return null;
        }

        var q = db.CatalogPackages.AsNoTracking();
        var text = search?.Trim() ?? string.Empty;
        if (text.Length > 0)
        {
            var like = $"%{text}%";
            q = q.Where(c => EF.Functions.ILike(c.Name, like) || EF.Functions.ILike(c.WingetId, like) || EF.Functions.ILike(c.Moniker ?? string.Empty, like)
                             || EF.Functions.ILike(c.Tags, like) || EF.Functions.ILike(c.Publisher ?? string.Empty, like));
        }

        var lower = text.ToLowerInvariant();
        var starts = lower + "%";
        var last = "." + lower;
        var word = "% " + lower + "%";
        var tail = "%-" + lower + "%";
        var end = "%" + lower;
        var matching = await q.CountAsync(ct);
        // The program itself before its variants: "firefox" finds Mozilla.Firefox before Mozilla.Firefox.af (they share the moniker); a name with the word
        // ("zip" in "7-Zip", "Bandizip"…) before names that only contain the letters somewhere ("Cram" by its tags).
        var rows = await q
            .OrderBy(c => c.WingetId.ToLower() == lower ? 0
                : c.WingetId.ToLower().EndsWith(last) ? 1
                : c.Moniker == lower ? 2
                : EF.Functions.ILike(c.Name, starts) ? 3
                : EF.Functions.ILike(c.Name, word) || EF.Functions.ILike(c.Name, tail) || EF.Functions.ILike(c.Name, end) ? 4
                : EF.Functions.ILike(c.WingetId, starts) ? 5 : 6)
            .ThenBy(c => c.Name.Length)
            .ThenBy(c => c.Name)
            .Skip((Math.Max(1, page) - 1) * pageSize).Take(pageSize)
            .Select(c => new
            {
                c.WingetId, c.Name, c.Version, c.Moniker, c.Tags,
                Package = db.Packages.Where(p => p.WingetId != null && p.WingetId.ToLower() == c.WingetId.ToLower()).Select(p => (long?)p.Id).FirstOrDefault(),
            })
            .ToListAsync(ct);
        return (rows.Select(r => new CatalogRow(r.WingetId, r.Name, r.Version, r.Moniker, r.Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(6).ToList(), r.Package)).ToList(), matching);
    }

    /// <summary>The details of a program from its manifest (loaded once, kept in the data volume).</summary>
    public async Task<CatalogDetail?> DetailAsync(AppDbContext db, Actor actor, string wingetId, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.PackagesView))
        {
            return null;
        }

        var item = await db.CatalogPackages.AsNoTracking().FirstOrDefaultAsync(c => c.WingetId == wingetId, ct);
        if (item is null)
        {
            return null;
        }

        var yaml = await ManifestAsync(item.ManifestPath, ct);
        var root = Parse(yaml);
        string? S(YamlMappingNode? node, string key) => node is not null && node.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlScalarNode s ? s.Value : null;
        YamlMappingNode? M(YamlMappingNode? node, string key) => node is not null && node.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v as YamlMappingNode : null;
        IEnumerable<YamlNode> L(YamlMappingNode? node, string key) => node is not null && node.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlSequenceNode seq ? seq.Children : [];

        var defaultType = S(root, "InstallerType");
        var defaultSilent = S(M(root, "InstallerSwitches"), "Silent");
        var defaultScope = S(root, "Scope");
        var defaultLocale = S(root, "InstallerLocale");
        var installers = L(root, "Installers").OfType<YamlMappingNode>().Select((i, n) =>
        {
            var type = S(i, "InstallerType") ?? defaultType ?? "exe";
            if (type == "zip" && S(i, "NestedInstallerType") is { } nested)
            {
                type = "zip/" + nested;
            }

            return new CatalogInstaller(n, S(i, "Architecture"), type, S(i, "Scope") ?? defaultScope, S(i, "InstallerLocale") ?? defaultLocale, S(i, "InstallerUrl") ?? string.Empty,
                S(i, "InstallerSha256")?.ToLowerInvariant(), S(M(i, "InstallerSwitches"), "Silent") ?? defaultSilent, S(i, "ProductCode"));
        }).Where(i => i.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)).ToList();
        var icon = L(root, "Icons").OfType<YamlMappingNode>().FirstOrDefault(i => S(i, "IconFileType") is "png" or "ico" && S(i, "IconTheme") is null or "default");
        var display = L(root, "AppsAndFeaturesEntries").OfType<YamlMappingNode>().Select(e => S(e, "DisplayName")).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));
        var package = await db.Packages.AsNoTracking().Where(p => p.WingetId != null && p.WingetId.ToLower() == wingetId.ToLower()).Select(p => (long?)p.Id).FirstOrDefaultAsync(ct);
        if (icon is not null && S(icon, "IconUrl") is { } iconUrl && S(icon, "IconSha256") is { } iconSha)
        {
            await IconAsync(iconSha.ToLowerInvariant(), S(icon, "IconFileType")!, iconUrl, ct);
        }

        return new CatalogDetail(
            item.WingetId, S(root, "PackageName") ?? item.Name, S(root, "PackageVersion") ?? item.Version, S(root, "Publisher"), S(root, "ShortDescription"), S(root, "Description"),
            S(root, "License"), S(root, "PackageUrl"), S(root, "PublisherUrl"), icon is null ? null : S(icon, "IconSha256")?.ToLowerInvariant(), display,
            L(root, "Tags").OfType<YamlScalarNode>().Select(t => t.Value!).Take(12).ToList(), installers, package);
    }

    private static YamlMappingNode? Parse(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
            return stream.Documents.FirstOrDefault()?.RootNode as YamlMappingNode;
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return null;
        }
    }

    private async Task<string> ManifestAsync(string manifestPath, CancellationToken ct)
    {
        var cache = Path.Combine(Root, "manifests", Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(manifestPath)))[..32] + ".yaml");
        if (File.Exists(cache))
        {
            return await File.ReadAllTextAsync(cache, ct);
        }

        using var client = http.CreateClient("winget");
        var yaml = await client.GetStringAsync(Cdn + manifestPath, ct);
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        await File.WriteAllTextAsync(cache, yaml, ct);
        return yaml;
    }

    /// <summary>The icon of a program, kept in the data volume by its SHA-256 (the manifest names it; a download that does not match is dropped).</summary>
    private async Task IconAsync(string sha, string type, string url, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(sha, "^[0-9a-f]{64}$") || IconPath(sha) is not null)
        {
            return;
        }

        try
        {
            using var client = http.CreateClient("winget");
            var bytes = await client.GetByteArrayAsync(url, ct);
            if (bytes.Length < 2_000_000 && Convert.ToHexStringLower(SHA256.HashData(bytes)) == sha)
            {
                Directory.CreateDirectory(Path.Combine(Root, "icons"));
                await File.WriteAllBytesAsync(Path.Combine(Root, "icons", sha + "." + (type == "png" ? "png" : "ico")), bytes, ct);
            }
        }
        catch (HttpRequestException)
        {
            // no icon: the page shows a box
        }
    }

    public string? IconPath(string sha)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(sha, "^[0-9a-f]{64}$"))
        {
            return null;
        }

        return new[] { "png", "ico" }.Select(e => Path.Combine(Root, "icons", sha + "." + e)).FirstOrDefault(File.Exists);
    }

    // ---- a program becomes a package, a new version of the catalog a version of it ------------------------------------------------------

    /// <summary>The kind of package an installer type of winget becomes when it is kept in MatHub (null: only through winget).</summary>
    public static PackageKind? PackageKindOf(string type) => type switch
    {
        "msi" or "wix" => PackageKind.Msi,
        "exe" or "nullsoft" or "inno" or "burn" => PackageKind.Exe,
        _ => null,
    };

    /// <summary>The silent switches of an installer: those of the manifest, else the ones of its kind.</summary>
    public static string? SilentArguments(CatalogInstaller installer) => installer.Type switch
    {
        "msi" or "wix" => null,
        _ when !string.IsNullOrWhiteSpace(installer.Silent) => installer.Silent.Trim(),
        "nullsoft" => "/S",
        "inno" => "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-",
        "burn" => "/quiet /norestart",
        _ => null,
    };

    /// <summary>The platform of an installer of the manifest (null: one MatHub does not distribute, e.g. arm32).</summary>
    public static InstallerArchitecture? ArchitectureOf(string? architecture) => architecture?.ToLowerInvariant() switch
    {
        "x64" => InstallerArchitecture.X64,
        "x86" => InstallerArchitecture.X86,
        "arm64" => InstallerArchitecture.Arm64,
        "neutral" or null => InstallerArchitecture.Neutral,
        _ => null,
    };

    /// <summary>
    /// The installer of each platform MatHub takes from a manifest: for the computer (not one user) first, a language-neutral or English or German one, and one
    /// that can be kept in MatHub (MSI before EXE).
    /// </summary>
    public static IReadOnlyList<(InstallerArchitecture Architecture, CatalogInstaller Installer)> Choose(IReadOnlyList<CatalogInstaller> installers)
    {
        static int Rank(CatalogInstaller i) =>
            (i.Scope == "user" ? 100 : 0)
            + (i.Locale is null or "en-US" or "de-DE" ? 0 : 10)
            + (PackageKindOf(i.Type) switch { PackageKind.Msi => 0, PackageKind.Exe => 1, _ => 5 });
        return installers.Select(i => (Architecture: ArchitectureOf(i.Architecture), Installer: i))
            .Where(x => x.Architecture is not null)
            .GroupBy(x => x.Architecture!.Value)
            .Select(g => (g.Key, g.OrderBy(x => Rank(x.Installer)).First().Installer))
            .OrderBy(x => x.Key)
            .ToList();
    }

    /// <summary>
    /// A program of the catalog as a package of MatHub: the package (its detection by the winget id, new versions prepared by themselves) and its current
    /// version, approved, with an installer for every platform – kept in MatHub, or (<paramref name="viaWinget"/>, or when there is no file) winget with this
    /// version pinned. A program that is a package already gets the version of the catalog as a new one (not approved).
    /// </summary>
    public async Task<Outcome<Package>> AddAsync(AppDbContext db, Actor actor, string wingetId, bool viaWinget, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.PackagesEdit))
        {
            return Outcome<Package>.Denied();
        }

        if (await DetailAsync(db, actor, wingetId, ct) is not { } d)
        {
            return Outcome<Package>.Denied();
        }

        // the installers first: a download that fails leaves nothing half made behind (no package, no version without installers)
        var downloads = await DownloadAllAsync(d, viaWinget, ct);
        if (downloads.Error is not null)
        {
            return Outcome<Package>.Fail(downloads.Error);
        }

        try
        {
            Package package;
            var status = VersionStatus.Approved;
            if (d.PackageId is { } existing && await db.Packages.FirstOrDefaultAsync(p => p.Id == existing, ct) is { } found)
            {
                package = found;
                status = VersionStatus.New;
                if (await db.PackageVersions.AnyAsync(v => v.PackageId == package.Id && v.Version == d.Version, ct))
                {
                    return Outcome<Package>.Success(package);
                }
            }
            else
            {
                var saved = await PackageService.SaveAsync(db, actor, null, new PackageForm(d.Name, d.Publisher, d.ShortDescription ?? d.Description, d.WingetId, DetectionKind.WingetId, d.WingetId, true), ct);
                if (!saved.Ok)
                {
                    return saved;
                }

                package = saved.Value!;
            }

            var version = await CreateVersionAsync(db, actor, package, d, downloads.Items, status, "store", ct);
            return version.Ok ? Outcome<Package>.Success(package) : Outcome<Package>.Fail(version.Error!);
        }
        finally
        {
            DeleteTemp(downloads.Items);
        }
    }

    /// <summary>An installer of the manifest as MatHub takes it: its platform, the kind (null: winget pinned) and the file downloaded and checked.</summary>
    private sealed record Download(InstallerArchitecture Architecture, CatalogInstaller Installer, PackageKind? Kind, string? Temp);

    /// <summary>Downloads the installers MatHub keeps (one per platform) and checks them; on an error nothing is left on disk.</summary>
    private async Task<(List<Download> Items, string? Error)> DownloadAllAsync(CatalogDetail d, bool viaWinget, CancellationToken ct)
    {
        var items = new List<Download>();
        foreach (var (architecture, installer) in Choose(d.Installers))
        {
            var kind = viaWinget ? null : PackageKindOf(installer.Type);
            if (kind is null)
            {
                items.Add(new Download(architecture, installer, null, null));
                continue;
            }

            var temp = await DownloadAsync(installer, ct);
            if (temp.Error is not null)
            {
                DeleteTemp(items);
                return ([], temp.Error);
            }

            items.Add(new Download(architecture, installer, kind, temp.Path));
        }

        return (items, null);
    }

    private static void DeleteTemp(IEnumerable<Download> items)
    {
        foreach (var item in items.Where(i => i.Temp is not null && File.Exists(i.Temp)))
        {
            File.Delete(item.Temp!);
        }
    }

    /// <summary>A version of a package from the manifest of the catalog with the installers downloaded before; on an error the version is removed again.</summary>
    private async Task<Outcome<PackageVersion>> CreateVersionAsync(AppDbContext db, Actor actor, Package package, CatalogDetail d, List<Download> downloads, VersionStatus status, string source, CancellationToken ct)
    {
        var created = await PackageVersions.CreateAsync(db, actor, package.Id, d.Version, source, null, ct);
        if (!created.Ok)
        {
            return created;
        }

        var version = created.Value!;
        foreach (var item in downloads)
        {
            Outcome<PackageInstaller> added;
            if (item.Kind is not { } kind)
            {
                added = await PackageVersions.AddInstallerAsync(db, actor, packageFiles, version.Id, new InstallerForm(item.Architecture, PackageKind.Winget,
                    WingetId: d.WingetId, WingetVersion: d.Version, SourceUrl: item.Installer.Url), null, null, ct);
            }
            else
            {
                var fileName = Path.GetFileName(new Uri(item.Installer.Url).LocalPath);
                if (string.IsNullOrWhiteSpace(Path.GetExtension(fileName)) || Path.GetExtension(fileName).ToLowerInvariant() is not (".msi" or ".exe"))
                {
                    fileName = d.WingetId + (kind == PackageKind.Msi ? ".msi" : ".exe");
                }

                await using var content = File.OpenRead(item.Temp!);
                added = await PackageVersions.AddInstallerAsync(db, actor, packageFiles, version.Id, new InstallerForm(item.Architecture, kind,
                    Arguments: SilentArguments(item.Installer), ProductCode: item.Installer.ProductCode, SourceUrl: item.Installer.Url), fileName, content, ct);
            }

            if (!added.Ok)
            {
                await PackageVersions.DeleteAsync(db, Actor.System, packageFiles, version.Id, ct);
                return Outcome<PackageVersion>.Fail(added.Error!);
            }
        }

        if (status != VersionStatus.New && downloads.Count > 0)
        {
            var set = await PackageVersions.SetStatusAsync(db, actor, version.Id, status, ct);
            if (!set.Ok)
            {
                return set;
            }
        }

        return Outcome<PackageVersion>.Success(version);
    }

    /// <summary>Downloads an installer of a manifest to a temporary file and checks it against the SHA-256 of the manifest (at most half an hour).</summary>
    private async Task<(string? Path, string? Error)> DownloadAsync(CatalogInstaller installer, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), "mathub-store-" + Guid.NewGuid().ToString("N"));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            using (var client = http.CreateClient("winget"))
            using (var response = await client.GetAsync(installer.Url, HttpCompletionOption.ResponseHeadersRead, limit.Token))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(limit.Token);
                await using var file = File.Create(temp);
                await source.CopyToAsync(file, limit.Token);
            }

            await using (var check = File.OpenRead(temp))
            {
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, ct));
                if (installer.Sha256 is { } expected && hash != expected)
                {
                    File.Delete(temp);
                    return (null, Localization.Text.T("Store.Error.Hash", hash[..12], expected[..12]));
                }
            }

            return (temp, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }

            return (null, Localization.Text.T("Store.Error.Download", ex is OperationCanceledException ? "timeout" : ex.Message));
        }
    }

    /// <summary>The newest version the catalog has of a package, when it is newer than every version of the package (null: nothing new).</summary>
    public static async Task<string?> NewerAsync(AppDbContext db, Package package, CancellationToken ct = default)
    {
        if (package.WingetId is null)
        {
            return null;
        }

        var id = package.WingetId.ToLower();
        var latest = await db.CatalogPackages.AsNoTracking().Where(c => c.WingetId.ToLower() == id).Select(c => c.Version).FirstOrDefaultAsync(ct);
        if (latest is null)
        {
            return null;
        }

        var known = await db.PackageVersions.AsNoTracking().Where(v => v.PackageId == package.Id).Select(v => v.Version).ToListAsync(ct);
        return known.Where(PackageVersions.Comparable).All(k => CompareVersions(latest, k) > 0) ? latest : null;
    }

    /// <summary>Takes the newest version of the catalog into a package as a new version (a person asks for it, or the daily check does).</summary>
    public async Task<Outcome<PackageVersion>> TakeNewerAsync(AppDbContext db, Actor actor, long packageId, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.PackagesEdit))
        {
            return Outcome<PackageVersion>.Denied();
        }

        var package = await db.Packages.FirstOrDefaultAsync(p => p.Id == packageId, ct);
        if (package?.WingetId is null || await NewerAsync(db, package, ct) is null || await DetailAsync(db, actor, package.WingetId, ct) is not { } d)
        {
            return Outcome<PackageVersion>.Fail(Localization.Text.T("Versions.Error.NothingNewer"));
        }

        // as the version before: kept in MatHub, or through winget
        var viaWinget = await db.PackageVersions.Where(v => v.PackageId == package.Id && v.Status == VersionStatus.Approved)
            .SelectMany(v => v.Installers).AnyAsync(i => i.Kind == PackageKind.Winget, ct);
        var downloads = await DownloadAllAsync(d, viaWinget, ct);
        if (downloads.Error is not null)
        {
            return Outcome<PackageVersion>.Fail(downloads.Error);
        }

        try
        {
            return await CreateVersionAsync(db, actor, package, d, downloads.Items, VersionStatus.New, "catalog", ct);
        }
        finally
        {
            DeleteTemp(downloads.Items);
        }
    }

    /// <summary>The daily check: every package that prepares its new versions by itself gets the newest one of the catalog, as a new version (never approved).</summary>
    public async Task<int> PrepareNewerAsync(AppDbContext db, CancellationToken ct = default)
    {
        var packages = await db.Packages.AsNoTracking().Where(p => p.AutoUpdate && p.WingetId != null).Select(p => p.Id).ToListAsync(ct);
        var prepared = 0;
        foreach (var id in packages)
        {
            var package = await db.Packages.AsNoTracking().FirstAsync(p => p.Id == id, ct);
            if (await NewerAsync(db, package, ct) is null)
            {
                continue;
            }

            try
            {
                var outcome = await TakeNewerAsync(db, Actor.System, id, ct);
                if (outcome.Ok)
                {
                    prepared++;
                    logger.LogInformation("A new version of {Package} is prepared: {Version}.", package.Name, outcome.Value!.Version);
                }
                else
                {
                    logger.LogWarning("The new version of {Package} could not be prepared: {Error}", package.Name, outcome.Error);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("The new version of {Package} could not be prepared: {Message}", package.Name, ex.Message);
            }
        }

        return prepared;
    }

    private static string? Cut(string? text, int max) => text is null ? null : text.Length > max ? text[..max] : text;
}
