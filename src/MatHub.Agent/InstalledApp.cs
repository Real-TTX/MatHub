namespace MatHub.Agent;

/// <summary>
/// The entry of the agent in Installed apps (MatDesign SETUP-04), for the machine because the agent is a service: the name, the publisher, the version, the icon
/// and the uninstall commands. The install writes it and the service writes it again at each start, so it shows the version after a self-update too; the
/// uninstall removes it. Plain C#: the tests compile it in.
/// </summary>
public static class InstalledApp
{
    /// <summary>Under <c>HKEY_LOCAL_MACHINE</c>.</summary>
    public const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + AgentInfo.ServiceName;

    /// <summary>The values: strings, and numbers that are DWORDs.</summary>
    public static IReadOnlyList<(string Name, object Value)> Values(string exe, string installDirectory, string version, string publisher, long bytes) =>
    [
        ("DisplayName", AgentInfo.DisplayName),
        ("DisplayVersion", version),
        ("Publisher", publisher),
        ("DisplayIcon", exe + ",0"),
        ("InstallLocation", installDirectory),
        // the uninstall opens its window (it asks for administrator rights itself); quiet: no window, the data kept (SETUP-05)
        ("UninstallString", $"\"{exe}\" uninstall"),
        ("QuietUninstallString", $"\"{exe}\" uninstall --quiet"),
        ("Comments", "Client management by MatHub: inventory, software, scripts."),
        ("EstimatedSize", (int)Math.Clamp(bytes / 1024, 0, int.MaxValue)),
        ("NoModify", 1),
        ("NoRepair", 1),
    ];
}
