// The protocol between the MatHub server and its agent (version 1). One source file, compiled into both projects (MatHub and MatHub.Agent), so the two
// never disagree. New fields are always optional: an older agent and a newer server (and the other way round) keep working.
//
//   POST /api/agent/v1/enroll                 EnrollRequest      -> EnrollResponse        (anonymous: the enrollment key or the token of a rollout)
//   POST /api/agent/v1/poll                   PollRequest        -> PollResponse          (headers X-MatHub-Client + X-MatHub-Key)
//   POST /api/agent/v1/runs/{runId}/result    RunResult          -> 204
//   POST /api/agent/v1/inventory              InventoryReport    -> 204
//   GET  /api/agent/v1/packages/{id}/file     the installer of a package
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

    public string? Manufacturer { get; init; }

    public string? Model { get; init; }

    public string? SerialNumber { get; init; }

    public string? Processor { get; init; }

    public long? MemoryBytes { get; init; }

    public string? LastUser { get; init; }

    public DateTime? BootTime { get; init; }

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
