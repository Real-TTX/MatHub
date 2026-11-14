using MatHub.Protocol;

namespace MatHub.Agent;

/// <summary>
/// The work of the service: call the server every few seconds (the server says how often), send the inventory when it is due, run the jobs one after the other
/// and report each one, and install a newer (or older) agent when the server offers one. A server that cannot be reached is asked again later; an agent the
/// server refuses (removed, switched off) waits five minutes between its calls.
/// </summary>
public sealed class AgentWorker(AgentConfig config, AgentClient client, JobRunner runner, Updater updater, AgentSignal signal, UiBroker broker, ILogger<AgentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("MatHub agent {Version} for {Server} started.", AgentInfo.Version, config.ServerUrl);
        Updater.CleanUp();
        var wait = TimeSpan.FromSeconds(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // the time the server asks for, or less: the kiosk started something
                await signal.WaitAsync(wait, stoppingToken);

                // results the server did not take last time (it was not there): first
                await SendKeptResultsAsync(stoppingToken);
                var poll = await client.PollAsync(new PollRequest
                {
                    AgentVersion = AgentInfo.Version,
                    HostName = Environment.MachineName,
                    LastUser = Inventory.LastUser(),
                    BootTime = Inventory.BootTime(),
                    UpdateStatus = Updater.LastStatus(),
                }, stoppingToken);
                wait = TimeSpan.FromSeconds(Math.Clamp(poll.PollSeconds, 10, 3600));
                if (poll.Status == "pending")
                {
                    continue;
                }

                if (poll.InventoryDue)
                {
                    await client.SendInventoryAsync(await Inventory.CollectAsync(stoppingToken), stoppingToken);
                    logger.LogInformation("Inventory sent.");
                }

                foreach (var job in poll.Jobs)
                {
                    var result = await runner.RunAsync(job, stoppingToken);
                    await ReportAsync(job.RunId, result, stoppingToken);
                    broker.JobEnded();
                }

                // jobs first: an update restarts the agent
                if (poll.Agent is { } release && release.Version != AgentInfo.Version && poll.Jobs.Count == 0)
                {
                    await updater.UpdateAsync(release, stoppingToken);
                }

                // there was work: ask again soon, the next job may be waiting
                if (poll.Jobs.Count > 0)
                {
                    wait = TimeSpan.FromSeconds(2);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (AgentRejectedException ex)
            {
                logger.LogWarning("{Message} The agent asks again in five minutes; enroll it again if it was removed.", ex.Message);
                wait = TimeSpan.FromMinutes(5);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                // the server is not there (yet): the time between the calls grows up to five minutes
                logger.LogWarning("The server cannot be reached: {Message}", ex.Message);
                wait = TimeSpan.FromSeconds(Math.Min(300, Math.Max(15, wait.TotalSeconds * 2)));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The agent ran into an error; it goes on.");
                wait = TimeSpan.FromSeconds(60);
            }
        }
    }

    /// <summary>The folder of the results that wait for the server (in the data folder of the agent: only SYSTEM and administrators).</summary>
    private static string KeptResults => Path.Combine(AgentInfo.DataDirectory, "results");

    /// <summary>Reports a result; when the server cannot be reached the result is kept on disk and reported with the next call (it is not lost).</summary>
    private async Task ReportAsync(long runId, RunResult result, CancellationToken ct)
    {
        try
        {
            await client.ReportAsync(runId, result, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            Directory.CreateDirectory(KeptResults);
            await File.WriteAllTextAsync(Path.Combine(KeptResults, runId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json"),
                System.Text.Json.JsonSerializer.Serialize(result), ct);
            logger.LogWarning("The result of run {Run} is kept until the server can be reached: {Message}", runId, ex.Message);
            throw;
        }
    }

    private async Task SendKeptResultsAsync(CancellationToken ct)
    {
        if (!Directory.Exists(KeptResults))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(KeptResults, "*.json").Order())
        {
            RunResult? result;
            try
            {
                result = System.Text.Json.JsonSerializer.Deserialize<RunResult>(await File.ReadAllTextAsync(file, ct));
            }
            catch (System.Text.Json.JsonException)
            {
                result = null;
            }

            if (!long.TryParse(Path.GetFileNameWithoutExtension(file), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var runId)
                || result is null)
            {
                File.Delete(file);
                continue;
            }

            await client.ReportAsync(runId, result, ct);
            File.Delete(file);
            logger.LogInformation("The kept result of run {Run} is reported.", runId);
        }
    }
}
