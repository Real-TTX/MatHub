using System.Diagnostics;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text.Json;
using MatHub.Protocol;

namespace MatHub.Agent;

/// <summary>
/// The agent updates itself to the version its server offers (up or down: the server's own build is the right one). The new file is downloaded next to the
/// installed one and checked against its SHA-256; then a copy of the running agent (<c>mathub-agent-updater.exe</c>) is started with <c>apply-update</c>: it
/// stops the service, renames the old file aside, puts the new one in its place and starts the service again. When the new agent does not start, the old one
/// is put back. A version that failed is not tried again for a day.
/// </summary>
public sealed class Updater(AgentClient client, ILogger<Updater> logger)
{
    private sealed record State(string Version, bool Succeeded, string Message, DateTime When);

    private static string StatePath => Path.Combine(AgentInfo.DataDirectory, "update.json");

    private static string LogPath => Path.Combine(AgentInfo.DataDirectory, "update.log");

    /// <summary>What the last update did, for the server ("1.2.3: done" / "1.2.3: failed …").</summary>
    public static string? LastStatus()
    {
        var state = Read();
        return state is null ? null : $"{state.Version}: {(state.Succeeded ? "done" : "failed")} {state.When:yyyy-MM-dd HH:mm}Z {state.Message}".Trim();
    }

    public async Task UpdateAsync(AgentRelease release, CancellationToken ct)
    {
        // only the installed agent replaces itself (a copy started by hand for a test does not)
        if (!string.Equals(Environment.ProcessPath, AgentInfo.InstalledExe, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Read() is { Succeeded: false } failed && failed.Version == release.Version && DateTime.UtcNow - failed.When < TimeSpan.FromDays(1))
        {
            return;
        }

        var staged = Path.Combine(AgentInfo.InstallDirectory, "mathub-agent.new.exe");
        logger.LogInformation("Updating the agent from {From} to {To}.", AgentInfo.Version, release.Version);
        await client.DownloadAsync(release.Url, staged, ct);
        await using (var stream = File.OpenRead(staged))
        {
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            if (!string.Equals(hash, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Write(new State(release.Version, false, "the download does not match its SHA-256", DateTime.UtcNow));
                File.Delete(staged);
                return;
            }
        }

        var updater = Path.Combine(AgentInfo.InstallDirectory, "mathub-agent-updater.exe");
        File.Copy(AgentInfo.InstalledExe, updater, overwrite: true);
        var start = new ProcessStartInfo(updater) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "apply-update", "--target", AgentInfo.InstalledExe, "--staged", staged, "--to", release.Version, "--service", AgentInfo.ServiceName })
        {
            start.ArgumentList.Add(a);
        }

        Process.Start(start);
        logger.LogInformation("The updater runs; the service restarts in a moment.");
    }

    /// <summary>The updater: swaps the files while the service is stopped, and puts the old one back when the new one does not start.</summary>
    public static int Apply(string target, string staged, string version, string service)
    {
        void Log(string text) => File.AppendAllText(LogPath, $"{DateTime.UtcNow:O} {text}{Environment.NewLine}");
        var old = target + ".old-" + DateTime.UtcNow.Ticks;
        try
        {
            Thread.Sleep(2000);
            using var controller = new ServiceController(service);
            Log($"update to {version}: stopping the service");
            if (controller.Status != ServiceControllerStatus.Stopped)
            {
                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
            }

            // the running file cannot be overwritten, but it can be renamed aside; try for a while (a scanner may hold it)
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(target, old);
                    break;
                }
                catch (IOException) when (attempt < 30)
                {
                    Thread.Sleep(1000);
                }
            }

            File.Move(staged, target);
            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
            Write(new State(version, true, string.Empty, DateTime.UtcNow));
            Log($"update to {version}: done");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"update to {version}: failed ({ex.Message}); the old agent goes back");
            Write(new State(version, false, ex.Message, DateTime.UtcNow));
            try
            {
                if (File.Exists(old))
                {
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }

                    File.Move(old, target);
                }

                using var controller = new ServiceController(service);
                if (controller.Status != ServiceControllerStatus.Running)
                {
                    controller.Start();
                }
            }
            catch (Exception restore)
            {
                Log("the old agent could not be put back: " + restore.Message);
            }

            return 1;
        }
    }

    /// <summary>At the start of the service: the files an update or a job left (the old agent, the updater, scripts, downloads).</summary>
    public static void CleanUp()
    {
        foreach (var pattern in new[] { "mathub-agent.exe.old-*", "mathub-agent-updater.exe", "mathub-agent.new.exe" })
        {
            foreach (var file in SafeFiles(AgentInfo.InstallDirectory, pattern))
            {
                TryDelete(() => File.Delete(file));
            }
        }

        foreach (var folder in new[] { "run", "packages" })
        {
            var dir = Path.Combine(AgentInfo.DataDirectory, folder);
            if (Directory.Exists(dir))
            {
                TryDelete(() => Directory.Delete(dir, recursive: true));
            }
        }
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern) => Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : [];

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // still in use: the next start tries again
        }
    }

    private static State? Read()
    {
        try
        {
            return File.Exists(StatePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Write(State state)
    {
        Directory.CreateDirectory(AgentInfo.DataDirectory);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
    }
}
