using MatHub.Protocol;

namespace MatHub.Agent;

/// <summary>
/// The work of the service: call the server every few seconds (the server says how often), send the inventory when it is due, run the jobs one after the other
/// and report each one, and install a newer (or older) agent when the server offers one. A server that cannot be reached is asked again later; an agent the
/// server refuses (removed, switched off) waits five minutes between its calls.
/// </summary>
public sealed class AgentWorker(AgentConfig config, AgentClient client, JobRunner runner, Updater updater, ILogger<AgentWorker> logger) : BackgroundService
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
                await Task.Delay(wait, stoppingToken);
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
                    await client.ReportAsync(job.RunId, result, stoppingToken);
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
}
