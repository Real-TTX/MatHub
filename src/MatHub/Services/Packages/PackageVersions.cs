using MatHub.Data;
using MatHub.Protocol;
using MatHub.Services.Access;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services.Packages;

/// <summary>An installer as a person enters or MatHub finds it.</summary>
public sealed record InstallerForm(
    InstallerArchitecture Architecture, PackageKind Kind, string? Arguments = null, string? UninstallCommand = null, string? InstallScript = null,
    string? WingetId = null, string? WingetVersion = null, string? ProductCode = null, string? SourceUrl = null);

/// <summary>A version for the pages: its installers and how many clients have it.</summary>
public sealed record VersionView(PackageVersion Version, IReadOnlyList<PackageInstaller> Installers, string? ApprovedBy, int Clients);

/// <summary>A choice of a job: a package with its approved version (<paramref name="VersionId"/> null) or with its version in test.</summary>
public sealed record JobVersionChoice(long PackageId, string Package, long? VersionId, string? Version, VersionStatus? Status)
{
    /// <summary>The value of the choice: <c>7</c> (the approved version of package 7) or <c>7@12</c> (its version 12).</summary>
    public string Value => VersionId is { } v ? FormattableString.Invariant($"{PackageId}@{v}") : FormattableString.Invariant($"{PackageId}");

    /// <summary>Reads a value back: the package and the version asked for.</summary>
    public static (long? Package, long? Version) Parse(string? value)
    {
        var parts = (value ?? string.Empty).Split('@', 2, StringSplitOptions.TrimEntries);
        var package = long.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : (long?)null;
        var version = parts.Length == 2 && long.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : (long?)null;
        return (package, version);
    }
}

/// <summary>
/// The versions of the packages (static business layer). A version is New (uploaded, or found in the catalog), in Test (the pilot group of the package gets it),
/// Approved (every assignment gets it: one approved version per package, the one before is retired) or Retired (kept for the way back). Each version has its
/// installers per platform; a job carries all of them and the agent takes the one that fits (<see cref="Architectures.Choose"/>).
/// </summary>
public static class PackageVersions
{
    /// <summary>A version MatHub can compare (it begins with a digit); "latest" of the packages from before the versions is none: there, installed is enough.</summary>
    public static bool Comparable(string? version) => version is { Length: > 0 } v && char.IsAsciiDigit(v[0]);

    /// <summary>A version as a person reads it: "latest" of the packages from before the versions is "the latest (winget)".</summary>
    public static string Show(string? version) => version is null ? "–" : Comparable(version) ? version : Localization.Text.T("Versions.Latest");

    public static string Code(InstallerArchitecture a) => a switch
    {
        InstallerArchitecture.X64 => Architectures.X64,
        InstallerArchitecture.X86 => Architectures.X86,
        InstallerArchitecture.Arm64 => Architectures.Arm64,
        _ => "neutral",
    };

    public static async Task<IReadOnlyList<VersionView>?> ListAsync(AppDbContext db, Actor actor, long packageId, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.PackagesView))
        {
            return null;
        }

        var package = await db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == packageId, ct);
        if (package is null)
        {
            return null;
        }

        var versions = await db.PackageVersions.AsNoTracking().Include(v => v.Installers).Where(v => v.PackageId == packageId).ToListAsync(ct);
        var installed = await InstalledAsync(db, actor, package, ct);
        var users = versions.Where(v => v.ApprovedUserId is not null).Select(v => v.ApprovedUserId!.Value).Distinct().ToList();
        var names = await db.Users.IgnoreQueryFilters().Where(u => users.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return versions
            .OrderBy(v => v.Status switch { VersionStatus.Testing => 0, VersionStatus.New => 1, VersionStatus.Approved => 2, _ => 3 })
            .ThenByDescending(v => v.Version, Comparer<string>.Create(WingetCatalog.CompareVersions))
            .Select(v => new VersionView(v, v.Installers.OrderBy(i => i.Architecture).ToList(), v.ApprovedUserId is { } u ? names.GetValueOrDefault(u) : null,
                installed.Count(i => i.Value is { } have && (!Comparable(v.Version) ? v.Status == VersionStatus.Approved : WingetCatalog.CompareVersions(have, v.Version) == 0))))
            .ToList();
    }

    /// <summary>The approved version of a package and the one in test (each may be missing), with their installers.</summary>
    /// <summary>
    /// What a job may install, package by package: its approved version (<c>VersionId</c> null: the job takes the approved one when it is made) and a version in
    /// test of its own (to try it on computers of one's choice). A package without both is listed without a version (the job says why it cannot run).
    /// </summary>
    public static async Task<IReadOnlyList<JobVersionChoice>> JobChoicesAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.PackagesView))
        {
            return [];
        }

        var packages = await db.Packages.AsNoTracking().OrderBy(p => p.Name).Select(p => new { p.Id, p.Name }).ToListAsync(ct);
        var current = await db.PackageVersions.AsNoTracking().Where(v => v.Status == VersionStatus.Approved || v.Status == VersionStatus.Testing)
            .OrderByDescending(v => v.Id).Select(v => new { v.Id, v.PackageId, v.Version, v.Status }).ToListAsync(ct);
        var list = new List<JobVersionChoice>();
        foreach (var p in packages)
        {
            var approved = current.FirstOrDefault(v => v.PackageId == p.Id && v.Status == VersionStatus.Approved);
            list.Add(new JobVersionChoice(p.Id, p.Name, null, approved?.Version, approved is null ? null : VersionStatus.Approved));
            if (current.FirstOrDefault(v => v.PackageId == p.Id && v.Status == VersionStatus.Testing) is { } testing)
            {
                list.Add(new JobVersionChoice(p.Id, p.Name, testing.Id, testing.Version, VersionStatus.Testing));
            }
        }

        return list;
    }

    public static async Task<(PackageVersion? Approved, PackageVersion? Testing)> CurrentAsync(AppDbContext db, long packageId, CancellationToken ct = default)
    {
        var current = await db.PackageVersions.AsNoTracking().Include(v => v.Installers)
            .Where(v => v.PackageId == packageId && (v.Status == VersionStatus.Approved || v.Status == VersionStatus.Testing))
            .OrderByDescending(v => v.Id).ToListAsync(ct);
        return (current.FirstOrDefault(v => v.Status == VersionStatus.Approved), current.FirstOrDefault(v => v.Status == VersionStatus.Testing));
    }

    /// <summary>The version each client of the package has (by the detection of the package; null: none).</summary>
    public static async Task<Dictionary<long, string?>> InstalledAsync(AppDbContext db, Actor actor, Package package, CancellationToken ct = default)
    {
        var reachable = AccessRules.Clients(db, actor).Select(c => c.Id);
        var rows = await PackageService.InstalledQuery(db, package.Detection, package.DetectionValue, package.WingetId)
            .Where(s => reachable.Contains(s.ClientId)).Select(s => new { s.ClientId, s.Version }).ToListAsync(ct);
        return rows.GroupBy(r => r.ClientId).ToDictionary(g => g.Key, g => g.Select(r => r.Version).Where(v => v is not null).OrderByDescending(v => v!, Comparer<string>.Create(WingetCatalog.CompareVersions)).FirstOrDefault());
    }

    /// <summary>A new version of a package (New, or Approved when it is the first one and the person says so).</summary>
    public static async Task<Outcome<PackageVersion>> CreateAsync(AppDbContext db, Actor actor, long packageId, string version, string source, string? notes = null, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.PackagesEdit))
        {
            return Outcome<PackageVersion>.Denied();
        }

        var package = await db.Packages.FirstOrDefaultAsync(p => p.Id == packageId, ct);
        var text = version.Trim();
        if (package is null)
        {
            return Outcome<PackageVersion>.Denied();
        }

        if (text.Length == 0)
        {
            return Outcome<PackageVersion>.Fail(Localization.Text.T("Versions.Error.Version"));
        }

        if (await db.PackageVersions.AnyAsync(v => v.PackageId == packageId && v.Version == text && v.Status != VersionStatus.Retired, ct))
        {
            return Outcome<PackageVersion>.Fail(Localization.Text.T("Versions.Error.Exists", text));
        }

        var created = new PackageVersion { PackageId = packageId, Version = text.Length > 100 ? text[..100] : text, Source = source, Notes = notes?.Trim() is { Length: > 0 } n ? n : null };
        db.PackageVersions.Add(created);
        ActivityLog.Add(db, actor, "package", "version", package.Name, created.Version);
        await db.SaveChangesAsync(ct);
        return Outcome<PackageVersion>.Success(created);
    }

    /// <summary>An installer of a version (a file is stored with it: MSI, EXE). Only a version that is not distributed yet takes new installers.</summary>
    public static async Task<Outcome<PackageInstaller>> AddInstallerAsync(
        AppDbContext db, Actor actor, PackageFiles store, long versionId, InstallerForm form, string? fileName, Stream? file, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.PackagesEdit))
        {
            return Outcome<PackageInstaller>.Denied();
        }

        var version = await db.PackageVersions.Include(v => v.Package).Include(v => v.Installers).FirstOrDefaultAsync(v => v.Id == versionId, ct);
        if (version?.Package is null)
        {
            return Outcome<PackageInstaller>.Denied();
        }

        if (version.Installers.Any(i => i.Architecture == form.Architecture))
        {
            return Outcome<PackageInstaller>.Fail(Localization.Text.T("Versions.Error.Platform", Code(form.Architecture)));
        }

        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        switch (form.Kind)
        {
            case PackageKind.Msi when file is null || extension != ".msi":
            case PackageKind.Exe when file is null || extension != ".exe":
                return Outcome<PackageInstaller>.Fail(Localization.Text.T("Packages.File.WrongType"));
            case PackageKind.Winget when string.IsNullOrWhiteSpace(form.WingetId):
                return Outcome<PackageInstaller>.Fail(Localization.Text.T("Packages.WingetId.Required"));
            case PackageKind.Script when string.IsNullOrWhiteSpace(form.InstallScript):
                return Outcome<PackageInstaller>.Fail(Localization.Text.T("Packages.InstallScript.Required"));
        }

        var installer = new PackageInstaller
        {
            PackageVersionId = version.Id, Architecture = form.Architecture, Kind = form.Kind,
            Arguments = Cut(form.Arguments, 1000), UninstallCommand = Cut(form.UninstallCommand, 4000), InstallScript = form.InstallScript?.Trim(),
            WingetId = Cut(form.WingetId, 200), WingetVersion = Cut(form.WingetVersion, 100), ProductCode = Cut(form.ProductCode, 60), SourceUrl = Cut(form.SourceUrl, 1000),
        };
        db.PackageInstallers.Add(installer);
        await db.SaveChangesAsync(ct);
        if (file is not null && fileName is not null)
        {
            var (name, size, sha) = await store.SaveAsync(version.PackageId, installer.Id, fileName, file, ct);
            installer.FileName = name;
            installer.FileSize = size;
            installer.FileSha256 = sha;
        }

        if (version.Status == VersionStatus.Approved)
        {
            version.Package.Kind = installer.Kind;
        }

        ActivityLog.Add(db, actor, "package", "upload", version.Package.Name, $"{version.Version} {Code(installer.Architecture)}");
        await db.SaveChangesAsync(ct);
        return Outcome<PackageInstaller>.Success(installer);
    }

    public static async Task<Outcome<string>> RemoveInstallerAsync(AppDbContext db, Actor actor, PackageFiles store, long installerId, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.PackagesEdit))
        {
            return Outcome<string>.Denied();
        }

        var installer = await db.PackageInstallers.Include(i => i.PackageVersion).FirstOrDefaultAsync(i => i.Id == installerId, ct);
        if (installer?.PackageVersion is null)
        {
            return Outcome<string>.Denied();
        }

        if (installer.PackageVersion.Status is VersionStatus.Approved or VersionStatus.Testing
            && await db.PackageInstallers.CountAsync(i => i.PackageVersionId == installer.PackageVersionId, ct) == 1)
        {
            return Outcome<string>.Fail(Localization.Text.T("Versions.Error.LastInstaller"));
        }

        db.PackageInstallers.Remove(installer);
        await db.SaveChangesAsync(ct);
        store.DeleteInstaller(installer.PackageVersion.PackageId, installer.Id);
        return Outcome<string>.Success(Code(installer.Architecture));
    }

    /// <summary>
    /// The state of a version. Approve: it is the version of every assignment from now on (the approved one before is retired; a version in test that is
    /// approved leaves the test). Test: the pilot group gets it (another version in test goes back to New). Retire: nobody gets it any more.
    /// </summary>
    public static async Task<Outcome<PackageVersion>> SetStatusAsync(AppDbContext db, Actor actor, long versionId, VersionStatus status, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.PackagesDeploy))
        {
            return Outcome<PackageVersion>.Denied();
        }

        var version = await db.PackageVersions.Include(v => v.Package).Include(v => v.Installers).FirstOrDefaultAsync(v => v.Id == versionId, ct);
        if (version?.Package is null)
        {
            return Outcome<PackageVersion>.Denied();
        }

        if (status is VersionStatus.Approved or VersionStatus.Testing && version.Installers.Count == 0)
        {
            return Outcome<PackageVersion>.Fail(Localization.Text.T("Versions.Error.NoInstaller"));
        }

        var others = await db.PackageVersions.Where(v => v.PackageId == version.PackageId && v.Id != version.Id && v.Status == status).ToListAsync(ct);
        foreach (var other in others)
        {
            other.Status = status == VersionStatus.Approved ? VersionStatus.Retired : VersionStatus.New;
        }

        version.Status = status;
        if (status == VersionStatus.Approved)
        {
            version.ApprovedDate = DateTime.UtcNow;
            version.ApprovedUserId = actor.UserId;
            version.Package.Version = version.Version;
            version.Package.Kind = version.Installers.OrderBy(i => i.Architecture).First().Kind;
        }
        else if (version.Package.Version == version.Version && status != VersionStatus.Testing)
        {
            version.Package.Version = null;
        }

        ActivityLog.Add(db, actor, "package", "status-" + status.ToString().ToLowerInvariant(), version.Package.Name, version.Version);
        await db.SaveChangesAsync(ct);
        return Outcome<PackageVersion>.Success(version);
    }

    public static async Task<Outcome<string>> DeleteAsync(AppDbContext db, Actor actor, PackageFiles store, long versionId, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.PackagesEdit))
        {
            return Outcome<string>.Denied();
        }

        var version = await db.PackageVersions.Include(v => v.Package).Include(v => v.Installers).FirstOrDefaultAsync(v => v.Id == versionId, ct);
        if (version?.Package is null)
        {
            return Outcome<string>.Denied();
        }

        if (version.Status is VersionStatus.Approved or VersionStatus.Testing)
        {
            return Outcome<string>.Fail(Localization.Text.T("Versions.Error.InUse"));
        }

        foreach (var installer in version.Installers)
        {
            store.DeleteInstaller(version.PackageId, installer.Id);
        }

        db.PackageVersions.Remove(version);
        ActivityLog.Add(db, actor, "package", "delete", version.Package.Name, version.Version);
        await db.SaveChangesAsync(ct);
        return Outcome<string>.Success(version.Version);
    }

    // ---- what the agent runs ----------------------------------------------------------------------------------------------------------

    /// <summary>The address the agent downloads the file of an installer from (relative to the server it talks to).</summary>
    public static string FileUrl(long installerId) => $"/api/agent/v1/installers/{installerId}/file";

    /// <summary>What the agent runs to install or remove one installer (null: it cannot, e.g. an EXE without an uninstall command).</summary>
    public static AgentJob? Template(PackageInstaller i, bool uninstall)
    {
        var architecture = i.Architecture == InstallerArchitecture.Neutral ? null : Code(i.Architecture);
        AgentJob? job = (i.Kind, uninstall) switch
        {
            (PackageKind.Winget, false) when i.WingetId is not null => new AgentJob { Kind = JobKinds.WingetInstall, Arguments = i.WingetId, Version = i.WingetVersion },
            (PackageKind.Winget, true) when i.WingetId is not null => new AgentJob { Kind = JobKinds.WingetUninstall, Arguments = i.WingetId },
            (PackageKind.Msi, false) when i.FileName is not null => new AgentJob { Kind = JobKinds.MsiInstall, DownloadUrl = FileUrl(i.Id), Sha256 = i.FileSha256, FileName = i.FileName, Arguments = i.Arguments },
            (PackageKind.Msi, true) when i.ProductCode is { } code && System.Text.RegularExpressions.Regex.IsMatch(code, @"^\{[0-9A-Fa-f-]{36}\}$")
                => new AgentJob { Kind = JobKinds.Command, Arguments = $"msiexec.exe /x {code} /qn /norestart" },
            (PackageKind.Msi, true) when i.FileName is not null => new AgentJob { Kind = JobKinds.MsiUninstall, DownloadUrl = FileUrl(i.Id), Sha256 = i.FileSha256, FileName = i.FileName },
            (PackageKind.Exe, false) when i.FileName is not null => new AgentJob { Kind = JobKinds.ExeInstall, DownloadUrl = FileUrl(i.Id), Sha256 = i.FileSha256, FileName = i.FileName, Arguments = i.Arguments },
            (PackageKind.Exe, true) when !string.IsNullOrWhiteSpace(i.UninstallCommand) => new AgentJob { Kind = JobKinds.Command, Arguments = i.UninstallCommand },
            (PackageKind.Script, false) when !string.IsNullOrWhiteSpace(i.InstallScript) => new AgentJob { Kind = JobKinds.Script, Script = i.InstallScript },
            (PackageKind.Script, true) when !string.IsNullOrWhiteSpace(i.UninstallCommand) => new AgentJob { Kind = JobKinds.Script, Script = i.UninstallCommand },
            _ => null,
        };
        return job is null ? null : job with { Architecture = architecture, TimeoutSeconds = 3600 };
    }

    /// <summary>
    /// The job of a version: every installer that can do it, the agent takes the one for its platform. The job itself is the one for x64 (or a neutral one),
    /// so an agent from before the platforms still runs something sensible.
    /// </summary>
    public static AgentJob? Template(PackageVersion version, bool uninstall)
    {
        var all = version.Installers.OrderBy(i => i.Architecture).Select(i => Template(i, uninstall)).Where(j => j is not null).Cast<AgentJob>().ToList();
        if (all.Count == 0)
        {
            return null;
        }

        var main = all.FirstOrDefault(j => j.Architecture == Architectures.X64) ?? all.FirstOrDefault(j => j.Architecture is null) ?? all[0];
        return all.Count == 1 ? main : main with { Alternatives = all };
    }

    private static string? Cut(string? text, int max)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > max ? t[..max] : t;
    }
}
