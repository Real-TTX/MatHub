using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace MatHub.Agent;

/// <summary>
/// The names, the version and the folders of the agent (MatDesign AGENT-01, SETUP-07). Plain .NET, no Windows type: the tests compile it in.
/// The version is the server's build: <c>0.1.42-20261112</c> (release), <c>nightly-42-20261112</c>, <c>local-20261112</c>.
/// </summary>
public static class AgentInfo
{
    /// <summary>The service, its display name, the entry of Installed apps (AGENT-01: <c>&lt;Product&gt; Agent</c>).</summary>
    public const string ServiceName = "MatHub Agent";

    /// <summary>What Installed apps, the tray and the windows call the program.</summary>
    public const string DisplayName = ServiceName;

    /// <summary>The autostart value of the tray in <c>HKLM\...\Run</c> (AGENT-01: <c>&lt;Product&gt; Agent Tray</c>).</summary>
    public const string TrayRunValue = "MatHub Agent Tray";

    /// <summary>The autostart value of agents installed before the desktop rules (it is renamed at the next start of the service).</summary>
    public const string OldTrayRunValue = "MatHub Agent";

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "local";

    /// <summary>The channel of this build: release, nightly or local (SETUP-07).</summary>
    public static string Channel => ChannelOf(Version);

    /// <summary>Who publishes the agent (Installed apps): the company of the build.</summary>
    public static string Publisher { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCompanyAttribute>()?.Company is { Length: > 0 } company ? company : "MatHub";

    /// <summary><c>Version 0.1.42-20261112 · release</c>: the footer of the windows (SETUP-07).</summary>
    public static string VersionText => FormatVersion(Version, Channel);

    /// <summary><c>0.1.42-20261112 · release</c>: the value of a row labelled Version (Info).</summary>
    public static string VersionValue => $"{Version} · {Channel}";

    /// <summary>The channel a version belongs to, by its form (Directory.Build.props): <c>nightly-…</c>, <c>local-…</c>, anything else is a release.</summary>
    public static string ChannelOf(string version) =>
        version.StartsWith("nightly-", StringComparison.OrdinalIgnoreCase) ? "nightly"
        : version.StartsWith("local", StringComparison.OrdinalIgnoreCase) ? "local"
        : "release";

    public static string FormatVersion(string version, string channel) => $"Version {version} · {channel}";

    /// <summary>
    /// Where the agent keeps its settings, its log and its downloads: <c>%ProgramData%\MatHub Agent</c>; <c>MATHUB_AGENT_DATA</c> names another folder (a test as a
    /// normal user: then the folder keeps the rights it has).
    /// </summary>
    public static string DataDirectory => OwnDataDirectory
        ? Environment.GetEnvironmentVariable("MATHUB_AGENT_DATA")!
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ServiceName);

    /// <summary>The data folder is named by <c>MATHUB_AGENT_DATA</c> (a test, not the service).</summary>
    public static bool OwnDataDirectory => Environment.GetEnvironmentVariable("MATHUB_AGENT_DATA") is { Length: > 0 };

    /// <summary>Where the installed agent lives: <c>%ProgramFiles%\MatHub Agent\mathub-agent.exe</c>.</summary>
    public static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ServiceName);

    public static string InstalledExe => Path.Combine(InstallDirectory, "mathub-agent.exe");

    /// <summary>
    /// The pipe of the service for its kiosk and the dialogs of scripts. A test with its own data folder has a pipe of its own (named by a hash of the folder),
    /// so it never meets an installed agent. (Its name stays as it was: a tray of the version before an update still finds the new service.)
    /// </summary>
    public static string PipeName => OwnDataDirectory ? "MatHubAgent-" + DataHash : "MatHubAgent";

    /// <summary>
    /// The status channel (AGENT-01: <c>&lt;product&gt;-agent-status</c>): a snapshot of the state for the tray, never a secret (AGENT-03). A test has its own,
    /// like <see cref="PipeName"/>.
    /// </summary>
    public static string StatusPipeName => OwnDataDirectory ? "mathub-agent-status-" + DataHash : "mathub-agent-status";

    private static string DataHash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(DataDirectory.ToLowerInvariant())))[..12];
}
