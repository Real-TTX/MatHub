using System.Security.Cryptography;
using System.Text;
using MatHub.Protocol;

namespace MatHub.Agent;

/// <summary>
/// Runs the jobs of the server, one after the other, as SYSTEM (the account of the service). Scripts are written to a file of their own in the data folder of
/// the agent (only SYSTEM and the administrators may read it) and deleted afterwards. A job ends with its exit code; 0 counts as done (installers: also 3010
/// and 1641, "restart needed"; winget: also "nothing to do").
/// </summary>
public sealed class JobRunner(AgentClient client, ILogger<JobRunner> logger)
{
    private static readonly int[] InstallerOk = [0, 3010, 1641];

    public async Task<RunResult> RunAsync(AgentJob job, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(job.TimeoutSeconds, 10, 86400));
        logger.LogInformation("Job {RunId}: {Kind}", job.RunId, job.Kind);
        try
        {
            return job.Kind switch
            {
                JobKinds.Script => await ScriptAsync(job.Language, job.Script ?? string.Empty, timeout, ct),
                JobKinds.WingetInstall => await WingetAsync(["install", "--id", Need(job.Arguments), "--exact", "--silent", "--scope", "machine", "--accept-package-agreements"], timeout, ct, retryWithoutScope: true),
                JobKinds.WingetUpgrade => string.IsNullOrWhiteSpace(job.Arguments)
                    ? await WingetAsync(["upgrade", "--all", "--silent", "--accept-package-agreements"], timeout, ct)
                    : await WingetAsync(["upgrade", "--id", job.Arguments, "--exact", "--silent", "--accept-package-agreements"], timeout, ct),
                JobKinds.WingetUninstall => await WingetAsync(["uninstall", "--id", Need(job.Arguments), "--exact", "--silent"], timeout, ct, notFoundIsOk: true),
                JobKinds.MsiInstall => await InstallerAsync(job, timeout, ct, file => ("msiexec.exe", $"/i \"{file}\" /qn /norestart {job.Arguments}".Trim()), InstallerOk),
                JobKinds.MsiUninstall => await InstallerAsync(job, timeout, ct, file => ("msiexec.exe", $"/x \"{file}\" /qn /norestart"), [.. InstallerOk, 1605]),
                JobKinds.ExeInstall => await InstallerAsync(job, timeout, ct, file => (file, job.Arguments ?? string.Empty), InstallerOk),
                JobKinds.Command => Result(await ProcessRunner.RunRawAsync(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /s /c \"" + Need(job.Arguments) + "\"", timeout, ct), InstallerOk),
                JobKinds.Inventory => await InventoryAsync(ct),
                JobKinds.Restart => Result(await ProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), ["/r", "/t", "60", "/c", "MatHub restarts this computer in one minute."], timeout, ct), [0]),
                _ => new RunResult { Succeeded = false, Output = $"This agent ({AgentInfo.Version}) does not know the job \"{job.Kind}\". An update of the agent may help." },
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Job {RunId} failed", job.RunId);
            return new RunResult { Succeeded = false, Output = "MatHub agent: " + ex.Message };
        }
    }

    private static string Need(string? value) => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException("The job has no value.") : value.Trim();

    private static RunResult Result(ProcessResult r, int[] ok) => new() { Succeeded = r.ExitCode is { } code && ok.Contains(code), ExitCode = r.ExitCode, Output = r.Output };

    private static async Task<RunResult> ScriptAsync(string language, string script, TimeSpan timeout, CancellationToken ct)
    {
        var dir = Path.Combine(AgentInfo.DataDirectory, "run");
        Directory.CreateDirectory(dir);
        var name = Path.Combine(dir, Guid.NewGuid().ToString("N"));
        var environment = new Dictionary<string, string> { ["MATHUB_WINGET"] = Winget.Locate() ?? string.Empty, ["MATHUB_AGENT"] = AgentInfo.Version };
        try
        {
            switch (language)
            {
                case "cmd":
                    // cmd reads and writes in the code page of the console: the script switches it to UTF-8 first (written without a byte order mark)
                    var cmd = name + ".cmd";
                    var batch = "@chcp 65001 >nul\r\n" + script.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
                    await File.WriteAllTextAsync(cmd, batch, new UTF8Encoding(false), ct);
                    return Result(await ProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", cmd], timeout, ct, environment), [0]);
                case "bash":
                    var sh = name + ".sh";
                    await File.WriteAllTextAsync(sh, script.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false), ct);
                    return Result(await ProcessRunner.RunAsync("bash", [sh], timeout, ct, environment), [0]);
                default:
                    // Windows PowerShell reads a script without a byte order mark in the code page of the system: UTF-8 with the mark keeps umlauts
                    var ps1 = name + ".ps1";
                    var wrapped = "[Console]::OutputEncoding = [Text.Encoding]::UTF8\r\n$ProgressPreference = 'SilentlyContinue'\r\n" + script.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
                    await File.WriteAllTextAsync(ps1, wrapped, new UTF8Encoding(true), ct);
                    return Result(await ProcessRunner.RunAsync(PowerShell.Path, ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", ps1], timeout, ct, environment), [0]);
            }
        }
        finally
        {
            foreach (var file in Directory.GetFiles(dir, Path.GetFileName(name) + ".*"))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // a process that still holds it: the next start of the agent clears the folder
                }
            }
        }
    }

    private static async Task<RunResult> WingetAsync(string[] arguments, TimeSpan timeout, CancellationToken ct, bool retryWithoutScope = false, bool notFoundIsOk = false)
    {
        if (Winget.Locate() is not { } winget)
        {
            return new RunResult { Succeeded = false, Output = "winget is not available on this computer (the App Installer of the Microsoft Store is missing)." };
        }

        var result = await ProcessRunner.RunAsync(winget, [.. arguments, .. Winget.Agreements], timeout, ct);
        if (retryWithoutScope && result.ExitCode == Winget.NoApplicableInstaller)
        {
            // a package with an installer for one user only: without --scope machine it is installed as SYSTEM, which is what winget allows
            var without = arguments.Where((a, i) => a != "--scope" && (i == 0 || arguments[i - 1] != "--scope")).ToArray();
            result = await ProcessRunner.RunAsync(winget, [.. without, .. Winget.Agreements], timeout, ct);
        }

        int[] ok = notFoundIsOk ? [.. Winget.Harmless, Winget.NotFound] : Winget.Harmless;
        return Result(result, ok);
    }

    /// <summary>Downloads the installer of a package from the server (checked against its SHA-256) and runs it.</summary>
    private async Task<RunResult> InstallerAsync(AgentJob job, TimeSpan timeout, CancellationToken ct, Func<string, (string File, string Arguments)> command, int[] ok)
    {
        var dir = Path.Combine(AgentInfo.DataDirectory, "packages", job.RunId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Path.GetFileName(job.FileName ?? "installer.bin"));
        try
        {
            await client.DownloadAsync(Need(job.DownloadUrl), file, ct);
            if (!string.IsNullOrEmpty(job.Sha256))
            {
                await using var stream = File.OpenRead(file);
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
                if (!string.Equals(hash, job.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new RunResult { Succeeded = false, Output = $"The download does not match (SHA-256 {hash}, expected {job.Sha256})." };
                }
            }

            var (exe, arguments) = command(file);
            return Result(await ProcessRunner.RunRawAsync(exe, arguments, timeout, ct, dir), ok);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // an installer that still runs a child: the next start clears it
            }
        }
    }

    private async Task<RunResult> InventoryAsync(CancellationToken ct)
    {
        var report = await Inventory.CollectAsync(ct);
        await client.SendInventoryAsync(report, ct);
        return new RunResult { Succeeded = true, ExitCode = 0, Output = $"Inventory sent: {report.Software.Count} programs, {report.WingetUpgrades?.Count ?? 0} updates." };
    }
}
