// The protocol between the MatHub server and its agent (version 1). One source file, compiled into both projects (MatHub and MatHub.Agent), so the two
// never disagree. New fields are always optional: an older agent and a newer server (and the other way round) keep working.
//
//   POST /api/agent/v1/enroll                 EnrollRequest      -> EnrollResponse        (anonymous: the enrollment key or the token of a rollout)
//   POST /api/agent/v1/poll                   PollRequest        -> PollResponse          (headers X-MatHub-Client + X-MatHub-Key)
//   POST /api/agent/v1/runs/{runId}/result    RunResult          -> 204
//   POST /api/agent/v1/inventory              InventoryReport    -> 204
//   GET  /api/agent/v1/packages/{id}/file     the installer of a package
//   GET  /api/agent/v1/kiosk                  -> KioskOffer            (the offers of the kiosk, asked live when it opens)
//   POST /api/agent/v1/kiosk/{id}/start       KioskStart         -> KioskStarted
//   GET  /api/agent/v1/runs/{runId}           -> RunState              (a run the kiosk started)
//   GET  /api/agent/v1/files/{id}             a file of a script that runs
//   GET  /agent/mathub-agent.exe              the agent itself (anonymous); /agent/install.ps1 installs it
namespace MatHub.Protocol;

public static class AgentHeaders
{
    public const string Client = "X-MatHub-Client";
    public const string Key = "X-MatHub-Key";
}

public sealed record EnrollRequest
{
    /// <summary>An enrollment key (<c>mhk_…</c>).</summary>
    public string? Key { get; init; }

    /// <summary>The token of a rollout task: the installation of a computer enrolls itself with it.</summary>
    public string? RolloutToken { get; init; }

    public string HostName { get; init; } = string.Empty;

    /// <summary>A hash of the MachineGuid: a reinstall of the agent finds its client again.</summary>
    public string? MachineId { get; init; }

    public string? OperatingSystem { get; init; }

    public string? AgentVersion { get; init; }
}

public sealed record EnrollResponse
{
    public string ClientToken { get; init; } = string.Empty;

    /// <summary>The secret of the agent: shown once, the server keeps only its hash.</summary>
    public string Secret { get; init; } = string.Empty;

    public string ClientName { get; init; } = string.Empty;

    /// <summary>The client waits for an approval in MatHub.</summary>
    public bool Pending { get; init; }
}

public sealed record PollRequest
{
    public string? AgentVersion { get; init; }

    public string? HostName { get; init; }

    public string? LastUser { get; init; }

    public DateTime? BootTime { get; init; }

    /// <summary>What the last self-update did (shown on the page of the client).</summary>
    public string? UpdateStatus { get; init; }
}

public sealed record PollResponse
{
    /// <summary>active, pending.</summary>
    public string Status { get; init; } = "active";

    public int PollSeconds { get; init; } = 30;

    /// <summary>Send an inventory now.</summary>
    public bool InventoryDue { get; init; }

    public IReadOnlyList<AgentJob> Jobs { get; init; } = [];

    /// <summary>The agent the server offers; another version than the running one is installed by the agent itself.</summary>
    public AgentRelease? Agent { get; init; }
}

public sealed record AgentRelease
{
    public string Version { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;
}

public static class JobKinds
{
    /// <summary>A script (<see cref="AgentJob.Language"/>: powershell, cmd, bash).</summary>
    public const string Script = "script";

    public const string WingetInstall = "winget-install";

    /// <summary>winget upgrade of <see cref="AgentJob.Arguments"/> (an id), or of everything when it is empty.</summary>
    public const string WingetUpgrade = "winget-upgrade";

    public const string WingetUninstall = "winget-uninstall";

    /// <summary>Downloads <see cref="AgentJob.DownloadUrl"/> and runs msiexec /i … /qn.</summary>
    public const string MsiInstall = "msi-install";

    public const string MsiUninstall = "msi-uninstall";

    /// <summary>Downloads <see cref="AgentJob.DownloadUrl"/> and runs it with <see cref="AgentJob.Arguments"/>.</summary>
    public const string ExeInstall = "exe-install";

    /// <summary>Runs <see cref="AgentJob.Arguments"/> as a command line.</summary>
    public const string Command = "command";

    /// <summary>Sends an inventory.</summary>
    public const string Inventory = "inventory";

    /// <summary>Restarts the computer after one minute.</summary>
    public const string Restart = "restart";
}

public sealed record AgentJob
{
    public long RunId { get; init; }

    public string Kind { get; init; } = JobKinds.Script;

    public string Language { get; init; } = "powershell";

    public string? Script { get; init; }

    public string? Arguments { get; init; }

    public string? DownloadUrl { get; init; }

    public string? Sha256 { get; init; }

    public string? FileName { get; init; }

    public int TimeoutSeconds { get; init; } = 600;

    /// <summary>The secrets the script names (name -> value; NAME_USER -> its user name): environment variables MATHUB_SECRET_NAME of the run, hidden in its output.</summary>
    public IReadOnlyDictionary<string, string>? Secrets { get; init; }

    /// <summary>What the job is called (the kiosk and its progress show it).</summary>
    public string? Title { get; init; }

    /// <summary>The files of the script: downloaded (and checked) into the folder it runs in, <c>MATHUB_FILES</c>.</summary>
    public IReadOnlyList<JobFile>? Files { get; init; }

    /// <summary>winget: exactly this version (<c>--version</c>); empty: the latest.</summary>
    public string? Version { get; init; }

    /// <summary>The platform of this installer: x64, x86, arm64 (null: every one). winget installs it with <c>--architecture</c>.</summary>
    public string? Architecture { get; init; }

    /// <summary>
    /// The same job for other platforms: the agent runs the one that fits its computer (arm64 → x64 → x86; a neutral one always fits). An agent that does not
    /// know them runs the job itself, which is the one for x64.
    /// </summary>
    public IReadOnlyList<AgentJob>? Alternatives { get; init; }
}

public static class Architectures
{
    public const string X64 = "x64";
    public const string X86 = "x86";
    public const string Arm64 = "arm64";

    /// <summary>The platforms that run on a computer, best first (an arm64 Windows runs x64 and x86 programs too).</summary>
    public static IReadOnlyList<string> Fitting(string computer) => computer switch
    {
        Arm64 => [Arm64, X64, X86],
        X64 => [X64, X86],
        _ => [X86],
    };

    /// <summary>The job of <paramref name="job"/> or its alternatives that fits the computer: the best platform, else a neutral one; null when none fits.</summary>
    public static AgentJob? Choose(AgentJob job, string computer)
    {
        if (job.Alternatives is not { Count: > 0 } all)
        {
            return job;
        }

        foreach (var platform in Fitting(computer))
        {
            if (all.FirstOrDefault(a => string.Equals(a.Architecture, platform, StringComparison.OrdinalIgnoreCase)) is { } fit)
            {
                return fit with { RunId = job.RunId, Title = job.Title, Secrets = fit.Secrets ?? job.Secrets };
            }
        }

        return all.FirstOrDefault(a => a.Architecture is null) is { } neutral ? neutral with { RunId = job.RunId, Title = job.Title } : null;
    }
}

/// <summary>A file a script brings along (an XML, a configuration): its name in the folder of the run and where the agent downloads it.</summary>
public sealed record JobFile
{
    public string Name { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public string? Sha256 { get; init; }

    public long Size { get; init; }
}

public sealed record RunResult
{
    public bool Succeeded { get; init; }

    public int? ExitCode { get; init; }

    public string? Output { get; init; }
}

public sealed record InventoryReport
{
    public string HostName { get; init; } = string.Empty;

    public string? Domain { get; init; }

    public string? OperatingSystem { get; init; }

    public string? OsVersion { get; init; }

    public string? OsBuild { get; init; }

    public string? Architecture { get; init; }

    /// <summary>The platform of Windows the packages are chosen for: x64, x86, arm64.</summary>
    public string? Platform { get; init; }

    public string? Manufacturer { get; init; }

    public string? Model { get; init; }

    public string? SerialNumber { get; init; }

    public string? Processor { get; init; }

    public long? MemoryBytes { get; init; }

    public string? LastUser { get; init; }

    public DateTime? BootTime { get; init; }

    public string? BiosVersion { get; init; }

    public DateTime? BiosDate { get; init; }

    public int? ProcessorCores { get; init; }

    public int? ProcessorThreads { get; init; }

    /// <summary>The names of the graphics adapters.</summary>
    public IReadOnlyList<string>? Graphics { get; init; }

    public DateTime? OsInstallDate { get; init; }

    /// <summary>Secure Boot on (null: not known, e.g. a BIOS without UEFI).</summary>
    public bool? SecureBoot { get; init; }

    public IReadOnlyList<DiskInfo> Disks { get; init; } = [];

    public IReadOnlyList<NetworkInfo> Network { get; init; } = [];

    public IReadOnlyList<SoftwareInfo> Software { get; init; } = [];

    /// <summary>What winget would update (null: winget is not there or failed).</summary>
    public IReadOnlyList<WingetUpgradeInfo>? WingetUpgrades { get; init; }

    /// <summary>The installed packages winget knows by id (null: winget is not there or failed).</summary>
    public IReadOnlyList<WingetPackageInfo>? WingetInstalled { get; init; }
}

public sealed record DiskInfo(string Name, string? FileSystem, long TotalBytes, long FreeBytes);

public sealed record NetworkInfo(string Name, string? MacAddress, IReadOnlyList<string> Addresses);

public sealed record SoftwareInfo(string Name, string? Version, string? Publisher);

public sealed record WingetUpgradeInfo(string Name, string Id, string? Version, string Available);

public sealed record WingetPackageInfo(string Name, string Id, string? Version);

// ---- the kiosk: offers the people at a computer start themselves ------------------------------------------------------------------

public sealed record KioskOffer
{
    public IReadOnlyList<KioskOfferItem> Items { get; init; } = [];
}

public sealed record KioskOfferItem
{
    public long Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string? Category { get; init; }

    /// <summary>The icon of the tile (a name of the icon set of the kiosk: box, download, wrench, printer, network, shield, user, terminal, …).</summary>
    public string Icon { get; init; } = "box";

    /// <summary>A question to answer before it starts.</summary>
    public string? Confirm { get; init; }

    /// <summary>script, package, sequence.</summary>
    public string Kind { get; init; } = "script";

    /// <summary>The last time it ran on this computer.</summary>
    public RunState? Last { get; init; }
}

public sealed record KioskStart
{
    /// <summary>Who started it (DOMAIN\user), for the activity log.</summary>
    public string? User { get; init; }
}

public sealed record KioskStarted
{
    public long RunId { get; init; }
}

public static class RunStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public sealed record RunState
{
    public long RunId { get; init; }

    /// <summary>queued, running, succeeded, failed, cancelled (<see cref="RunStates"/>).</summary>
    public string Status { get; init; } = RunStates.Queued;

    public int? ExitCode { get; init; }

    public DateTime? Finished { get; init; }

    /// <summary>A sequence: the step (1 = the first) and how many there are.</summary>
    public int Step { get; init; }

    public int Steps { get; init; }
}

// ---- dialogs: a script asks the person at the computer (the agent shows it in their session) -----------------------------------------

public static class UiKinds
{
    /// <summary>A message with OK.</summary>
    public const string Message = "message";

    /// <summary>A question: yes or no.</summary>
    public const string Question = "question";

    /// <summary>Fields on one or more pages (a wizard): the values come back.</summary>
    public const string Form = "form";

    /// <summary>A notice in the corner; the script goes on at once.</summary>
    public const string Notify = "notify";

    /// <summary>A window "please wait" that stays until the script ends or shows the next dialog.</summary>
    public const string Progress = "progress";
}

public sealed record UiDialog
{
    public string Kind { get; init; } = UiKinds.Message;

    public string? Title { get; init; }

    public string? Text { get; init; }

    /// <summary>A message: info, warning, error.</summary>
    public string? Level { get; init; }

    public IReadOnlyList<UiPage> Pages { get; init; } = [];

    /// <summary>Nobody answers in this time: the answer is "timeout".</summary>
    public int TimeoutSeconds { get; init; } = 300;
}

public sealed record UiPage
{
    public string? Title { get; init; }

    public string? Text { get; init; }

    public IReadOnlyList<UiField> Fields { get; init; } = [];
}

public sealed record UiField
{
    public string Name { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    /// <summary>text, password, choice, check, number, multiline, info (a text without an input).</summary>
    public string Kind { get; init; } = "text";

    public IReadOnlyList<string>? Options { get; init; }

    public string? Default { get; init; }

    public bool Required { get; init; }
}

public sealed record UiReply
{
    /// <summary>ok, cancel, yes, no, timeout, nobody (nobody is signed in, or the kiosk of the agent does not run).</summary>
    public string Answer { get; init; } = "cancel";

    public IReadOnlyDictionary<string, string>? Values { get; init; }
}
