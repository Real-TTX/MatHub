using System.Text.RegularExpressions;
using MatHub.Protocol;

namespace MatHub.Agent;

/// <summary>
/// The Windows Package Manager as the agent uses it. As SYSTEM winget is not on the PATH: it is found in the folder of the App Installer
/// (<c>%ProgramFiles%\WindowsApps\Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe\winget.exe</c>, the newest). Its tables (list, upgrade) are read by the
/// positions of the header, whatever language they are in.
/// </summary>
public static partial class Winget
{
    /// <summary>winget exit codes that mean "nothing to do", not an error.</summary>
    public static readonly int[] Harmless =
    [
        0,
        -1978335189, // 0x8A15002B: no applicable update (up to date)
        -1978335135, // 0x8A150061: the package is installed already
    ];

    /// <summary>0x8A150014: no package found (an uninstall of something that is not there).</summary>
    public const int NotFound = -1978335212;

    /// <summary>0x8A150010: no installer for the scope asked (a package for one user only): try again without <c>--scope machine</c>.</summary>
    public const int NoApplicableInstaller = -1978335216;

    private static string? _path;

    public static string? Locate()
    {
        if (_path is not null && File.Exists(_path))
        {
            return _path;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), "winget.exe");
            if (File.Exists(candidate))
            {
                return _path = candidate;
            }
        }

        var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        try
        {
            var found = Directory.GetDirectories(apps, "Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe")
                .Select(d => (Dir: d, Version: VersionOf(Path.GetFileName(d))))
                .OrderByDescending(d => d.Version)
                .Select(d => Path.Combine(d.Dir, "winget.exe"))
                .FirstOrDefault(File.Exists);
            return _path = found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Version VersionOf(string folder)
    {
        var parts = folder.Split('_');
        return parts.Length > 1 && Version.TryParse(parts[1], out var v) ? v : new Version(0, 0);
    }

    public static readonly string[] Agreements = ["--accept-source-agreements", "--disable-interactivity"];

    /// <summary>What winget would update.</summary>
    public static async Task<IReadOnlyList<WingetUpgradeInfo>?> UpgradesAsync(CancellationToken ct)
    {
        if (Locate() is not { } winget)
        {
            return null;
        }

        var result = await ProcessRunner.RunAsync(winget, ["upgrade", .. Agreements], TimeSpan.FromMinutes(3), ct);
        return result.ExitCode is null ? null : ParseTable(result.Output).Where(r => r.Length >= 4 && Id(r[1]) is not null && r[3].Length > 0)
            .Select(r => new WingetUpgradeInfo(r[0], r[1], r[2], r[3])).ToList();
    }

    /// <summary>The installed programs winget knows by id (those of the source winget only).</summary>
    public static async Task<IReadOnlyList<WingetPackageInfo>?> InstalledAsync(CancellationToken ct)
    {
        if (Locate() is not { } winget)
        {
            return null;
        }

        var result = await ProcessRunner.RunAsync(winget, ["list", "--source", "winget", .. Agreements], TimeSpan.FromMinutes(3), ct);
        return result.ExitCode is null ? null : ParseTable(result.Output).Where(r => r.Length >= 3 && Id(r[1]) is not null)
            .Select(r => new WingetPackageInfo(r[0], r[1], r[2])).ToList();
    }

    /// <summary>An id as winget writes it, or null for a cut one (<c>Microsoft.VisualStudio.2022.Commun…</c>) or a column that is no id.</summary>
    private static string? Id(string text) => IdRegex().IsMatch(text) && !text.EndsWith('…') ? text : null;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+\-]*$")]
    private static partial Regex IdRegex();

    [GeneratedRegex(@"\S+")]
    private static partial Regex WordRegex();

    /// <summary>
    /// The rows of the table winget writes: the line of dashes marks the header above it; the columns begin where the words of the header begin. A row ends at
    /// the first line that does not fit (the counts and notes below a table).
    /// </summary>
    public static IReadOnlyList<string[]> ParseTable(string output)
    {
        // winget draws its progress with carriage returns: of every line only what follows the last one is what a console shows
        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).Select(l => l[(l.LastIndexOf('\r') + 1)..]).ToList();
        var rows = new List<string[]>();
        for (var i = 1; i < lines.Count; i++)
        {
            if (!lines[i].TrimStart().StartsWith("---", StringComparison.Ordinal) || lines[i].Trim().Trim('-').Length > 0)
            {
                continue;
            }

            // the header may carry the rest of a progress spinner in front of it: the columns start where its words start
            var header = lines[i - 1];
            var starts = WordRegex().Matches(header).Select(m => m.Index).ToList();
            if (starts.Count < 3)
            {
                continue;
            }

            for (var j = i + 1; j < lines.Count; j++)
            {
                var line = lines[j];
                if (line.Trim().Length == 0 || line.Length < starts[1])
                {
                    break;
                }

                var cells = new string[starts.Count];
                for (var c = 0; c < starts.Count; c++)
                {
                    var from = Math.Min(starts[c], line.Length);
                    var to = c + 1 < starts.Count ? Math.Min(starts[c + 1], line.Length) : line.Length;
                    cells[c] = line[from..to].Trim();
                }

                rows.Add(cells);
            }
        }

        return rows;
    }
}
