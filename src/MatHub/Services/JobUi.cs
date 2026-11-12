using MatHub.Data;

namespace MatHub.Services;

/// <summary>How the pages show jobs, runs, clients and packages: icons, tones and the keys of their texts (one place, so every page says it the same way).</summary>
public static class JobUi
{
    public static string Icon(JobKind kind) => kind switch
    {
        JobKind.Script => "terminal",
        JobKind.PackageInstall => "download",
        JobKind.PackageUninstall => "trash",
        JobKind.WingetUpgrade => "refresh",
        JobKind.Inventory => "list-checks",
        JobKind.Restart => "power",
        JobKind.Sequence => "route",
        _ => "play",
    };

    /// <summary>The tone of a pill (ok, error, busy, plain).</summary>
    public static string Tone(RunStatus status) => status switch
    {
        RunStatus.Succeeded => "ok",
        RunStatus.Failed => "error",
        RunStatus.Running => "busy",
        RunStatus.Queued => "info",
        _ => "plain",
    };

    public static string StatusKey(RunStatus status) => "Runs.Status." + status;

    public static string PackageIcon(PackageKind kind) => kind switch
    {
        PackageKind.Winget => "box",
        PackageKind.Msi => "archive",
        PackageKind.Exe => "file",
        _ => "terminal",
    };

    public static string EditorIcon(ScriptEditor editor) => editor switch
    {
        ScriptEditor.Blocks => "blocks",
        ScriptEditor.Flow => "flow",
        _ => "code",
    };

    public static string ProfileIcon(ProfileKind kind) => kind switch
    {
        ProfileKind.Windows => "windows",
        ProfileKind.Linux => "linux",
        _ => "code",
    };

    public static string RolloutTone(RolloutStatus status) => status switch
    {
        RolloutStatus.Waiting => "info",
        RolloutStatus.Installing => "busy",
        RolloutStatus.Completed => "ok",
        _ => "plain",
    };
}
