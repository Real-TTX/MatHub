using System.Security.Cryptography;
using MatHub.Protocol;

namespace MatHub.Services.Agent;

/// <summary>
/// The Windows agent MatHub hands out: <c>mathub-agent.exe</c> in the folder <c>agent/</c> next to the app (the image builds it with the server, so both have the
/// same version), its SHA-256 and the install script. Without the file (a <c>dotnet run</c> of the server alone) there is nothing to hand out and the agents are
/// not offered an update.
/// </summary>
public sealed class AgentFiles(IConfiguration configuration, ILogger<AgentFiles> logger)
{
    public const string FileName = "mathub-agent.exe";

    private readonly object _gate = new();
    private (DateTime Written, long Length, string Sha256, string Version)? _cached;

    public string Directory => configuration["AgentDirectory"] ?? System.IO.Path.Combine(AppContext.BaseDirectory, "agent");

    public string Path => System.IO.Path.Combine(Directory, FileName);

    public bool Available => File.Exists(Path);

    /// <summary>The version of the agent: the file <c>agent.version</c> beside it, else the version of the server (they are built together).</summary>
    public string Version => Describe()?.Version ?? AppInfo.Version;

    public string? Sha256 => Describe()?.Sha256;

    public long? Length => Describe()?.Length;

    /// <summary>What the poll offers the agents (null without the file).</summary>
    public AgentRelease? Release() => Describe() is { } d ? new AgentRelease { Version = d.Version, Sha256 = d.Sha256, Url = "/agent/" + FileName } : null;

    private (DateTime Written, long Length, string Sha256, string Version)? Describe()
    {
        var file = new FileInfo(Path);
        if (!file.Exists)
        {
            return null;
        }

        lock (_gate)
        {
            if (_cached is { } c && c.Written == file.LastWriteTimeUtc && c.Length == file.Length)
            {
                return c;
            }

            using var stream = file.OpenRead();
            var sha = Convert.ToHexStringLower(SHA256.HashData(stream));
            var versionFile = System.IO.Path.Combine(Directory, "agent.version");
            var version = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : AppInfo.Version;
            _cached = (file.LastWriteTimeUtc, file.Length, sha, version);
            logger.LogInformation("Agent {Version} ({Sha256}) is offered to the clients.", version, sha);
            return _cached;
        }
    }

    /// <summary>
    /// The install script for an elevated PowerShell on Windows: downloads the agent, checks its SHA-256 and runs <c>mathub-agent.exe install</c>, which
    /// enrolls the computer and registers the service. Its parameters are the enrollment key or the token of a rollout.
    /// </summary>
    public string InstallScript(string server)
    {
        var sha = Sha256 ?? string.Empty;
        return InstallScriptTemplate.Replace("{SERVER}", server.Replace("'", "''"), StringComparison.Ordinal).Replace("{SHA256}", sha, StringComparison.Ordinal);
    }

    /// <summary>The line an administrator pastes into an elevated PowerShell.</summary>
    public static string InstallCommand(string server, string? key) =>
        $"& ([scriptblock]::Create((irm '{server}/agent/install.ps1'))) -Key '{key ?? "<KEY>"}'";

    /// <summary>The command the answer file of a rollout runs after the setup.</summary>
    public static string RolloutCommand(string server, Guid token) =>
        $"powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"& ([scriptblock]::Create((irm '{server}/agent/install.ps1'))) -RolloutToken '{token}'\"";

    private const string InstallScriptTemplate = """
        # MatHub agent: installs the agent as a Windows service and enrolls this computer.
        #   & ([scriptblock]::Create((irm '{SERVER}/agent/install.ps1'))) -Key 'mhk_...'
        param([string]$Key, [string]$RolloutToken)
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $server = '{SERVER}'
        $expected = '{SHA256}'

        $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'MatHub: please run this in a PowerShell as administrator.'
        }
        if (-not $Key -and -not $RolloutToken) { throw 'MatHub: an enrollment key is needed (-Key).' }
        if (-not $expected) { throw 'MatHub: the server has no agent to hand out.' }

        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $file = Join-Path $env:TEMP ('mathub-agent-' + [guid]::NewGuid().ToString('N') + '.exe')
        Write-Host 'MatHub: downloading the agent ...'
        Invoke-WebRequest -UseBasicParsing -Uri "$server/agent/mathub-agent.exe" -OutFile $file
        $hash = (Get-FileHash -Algorithm SHA256 -Path $file).Hash.ToLowerInvariant()
        if ($hash -ne $expected) {
            Remove-Item $file -Force
            throw "MatHub: the download does not match (SHA-256 $hash)."
        }

        $arguments = @('install', '--server', $server)
        if ($Key) { $arguments += @('--key', $Key) }
        if ($RolloutToken) { $arguments += @('--rollout', $RolloutToken) }
        & $file @arguments
        $code = $LASTEXITCODE
        Remove-Item $file -Force -ErrorAction SilentlyContinue
        if ($code -ne 0) { throw "MatHub: the installation failed (exit code $code)." }
        Write-Host 'MatHub: the agent is installed and running.'

        """;
}
