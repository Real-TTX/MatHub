using MatHub.Data;
using MatHub.Services.Access;
using MatHub.Services.Packages;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Tests;

/// <summary>The store: the index of winget is read as winget writes it (the latest version of every program, its path, tags), and the search finds it.</summary>
public sealed class StoreTests
{
    [Theory]
    [InlineData("10.2", "9.11", 1)]
    [InlineData("1.0.0", "1.0", 0)]
    [InlineData("24.08", "24.8", 0)]
    [InlineData("1.2.3-beta", "1.2.3", -1)]
    [InlineData("2.0.0-rc1", "2.0.0-rc2", -1)]
    [InlineData("1.2.beta", "1.2.0", -1)]
    [InlineData("9.1", "10.0", -1)]
    [InlineData("128.0", "129.0", -1)]
    public void Versions_are_compared_part_by_part(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(WingetCatalog.CompareVersions(a, b)));

    [Theory]
    [InlineData("nullsoft", null, "/S")]
    [InlineData("inno", null, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-")]
    [InlineData("exe", "/quiet", "/quiet")]
    [InlineData("msi", "/x", null)]
    [InlineData("exe", null, null)]
    public void The_silent_switches_come_from_the_manifest_or_the_kind(string type, string? silent, string? expected)
        => Assert.Equal(expected, WingetCatalog.SilentArguments(new CatalogInstaller(0, "x64", type, null, null, "https://x", null, silent, null)));

    [Fact]
    public void Only_msi_and_known_exe_installers_can_be_kept_in_mathub()
    {
        Assert.Equal(PackageKind.Msi, WingetCatalog.PackageKindOf("wix"));
        Assert.Equal(PackageKind.Exe, WingetCatalog.PackageKindOf("burn"));
        Assert.Null(WingetCatalog.PackageKindOf("msix"));
        Assert.Null(WingetCatalog.PackageKindOf("zip/msi"));
    }

    /// <summary>A small index in the schema of winget (version 1): two versions of one program, one of another.</summary>
    private static string Index()
    {
        var path = Path.Combine(Path.GetTempPath(), "mathub-index-" + Guid.NewGuid().ToString("N") + ".db");
        using var c = new SqliteConnection("Data Source=" + path + ";Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE ids(rowid INTEGER PRIMARY KEY, id TEXT NOT NULL);
            CREATE TABLE names(rowid INTEGER PRIMARY KEY, name TEXT NOT NULL);
            CREATE TABLE monikers(rowid INTEGER PRIMARY KEY, moniker TEXT NOT NULL);
            CREATE TABLE versions(rowid INTEGER PRIMARY KEY, version TEXT NOT NULL);
            CREATE TABLE pathparts(rowid INTEGER PRIMARY KEY, parent INT64, pathpart TEXT NOT NULL);
            CREATE TABLE manifest(rowid INTEGER PRIMARY KEY, id INT64, name INT64, moniker INT64, version INT64, channel INT64, pathpart INT64);
            CREATE TABLE tags(rowid INTEGER PRIMARY KEY, tag TEXT NOT NULL);
            CREATE TABLE tags_map(manifest INT64, tag INT64);
            CREATE TABLE norm_publishers(rowid INTEGER PRIMARY KEY, norm_publisher TEXT NOT NULL);
            CREATE TABLE norm_publishers_map(manifest INT64, norm_publisher INT64);
            INSERT INTO ids VALUES (1, 'Contoso.Editor'), (2, 'Fabrikam.Zip');
            INSERT INTO names VALUES (1, 'Contoso Editor'), (2, 'Fabrikam Zip');
            INSERT INTO monikers VALUES (1, 'editor');
            INSERT INTO versions VALUES (1, '9.11'), (2, '10.2'), (3, '1.0');
            INSERT INTO pathparts VALUES (1, NULL, 'manifests'), (2, 1, 'c'), (3, 2, 'Contoso'), (4, 3, 'Editor'), (5, 4, '10.2'), (6, 5, 'ab12'), (7, 4, '9.11'), (8, 7, 'cd34'),
                                         (9, 1, 'f'), (10, 9, 'Fabrikam'), (11, 10, 'Zip'), (12, 11, '1.0'), (13, 12, 'ef56');
            INSERT INTO manifest VALUES (1, 1, 1, 1, 1, 0, 8), (2, 1, 1, 1, 2, 0, 6), (3, 2, 2, 0, 3, 0, 13);
            INSERT INTO tags VALUES (1, 'text'), (2, 'old');
            INSERT INTO tags_map VALUES (2, 1), (1, 2);
            INSERT INTO norm_publishers VALUES (1, 'contoso');
            INSERT INTO norm_publishers_map VALUES (2, 1);
            """;
        cmd.ExecuteNonQuery();
        return path;
    }

    [Fact]
    public void The_index_gives_the_latest_version_with_its_manifest()
    {
        var path = Index();
        try
        {
            var programs = WingetCatalog.Read(path);
            Assert.Equal(2, programs.Count);
            var editor = programs.Single(p => p.WingetId == "Contoso.Editor");
            Assert.Equal("10.2", editor.Version);
            Assert.Equal("manifests/c/Contoso/Editor/10.2/ab12", editor.ManifestPath);
            Assert.Equal("text", editor.Tags);
            Assert.Equal("contoso", editor.Publisher);
            Assert.Equal("editor", editor.Moniker);
            Assert.Null(programs.Single(p => p.WingetId == "Fabrikam.Zip").Moniker);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [DbFact]
    public async Task The_search_puts_the_exact_program_first_and_knows_the_packages()
    {
        await using var t = await TestDatabase.CreateAsync();
        t.Db.CatalogPackages.AddRange(
            new CatalogPackage { WingetId = "Mozilla.Firefox", Name = "Mozilla Firefox", Moniker = "firefox", Version = "160.0", ManifestPath = "m" },
            new CatalogPackage { WingetId = "Mozilla.Firefox.ESR", Name = "Mozilla Firefox ESR", Version = "140.0", ManifestPath = "m" },
            new CatalogPackage { WingetId = "Contoso.FirefoxHelper", Name = "Helper for Firefox", Version = "1.0", ManifestPath = "m", Tags = "browser" });
        await t.Db.SaveChangesAsync();
        var admin = new Actor(1, true, new HashSet<string>(), true, []);
        await PackageService.SaveAsync(t.Db, admin, null, new PackageForm("Firefox", "Mozilla", null, "Mozilla.Firefox", DetectionKind.WingetId, null, true));

        var (items, matching) = (await WingetCatalog.SearchAsync(t.Db, admin, "firefox", 1, 10))!.Value;
        Assert.Equal(3, matching);
        Assert.Equal("Mozilla.Firefox", items[0].WingetId);
        Assert.NotNull(items[0].PackageId);
        Assert.Null(items[1].PackageId);
        Assert.Equal("Contoso.FirefoxHelper", Assert.Single((await WingetCatalog.SearchAsync(t.Db, admin, "browser", 1, 10))!.Value.Items).WingetId);
        Assert.Null(await WingetCatalog.SearchAsync(t.Db, new Actor(2, false, new HashSet<string>(), true, []), "firefox", 1, 10));
    }
}
