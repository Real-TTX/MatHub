using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace MatHub.Agent;

/// <summary>
/// The status channel of the service (MatDesign AGENT-01, AGENT-03): the pipe <c>mathub-agent-status</c>. Whoever connects gets one JSON line, the snapshot of
/// <see cref="AgentStatusBoard"/> (state, server, last contact, version, never a secret), and the pipe closes. It is the tray's way to show the state; the kiosk
/// and the dialogs of scripts keep their own pipe (<see cref="AgentInfo.PipeName"/>).
/// </summary>
public sealed class StatusPipe(AgentStatusBoard board, ILogger<StatusPipe> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = UiBroker.CreatePipe(AgentInfo.StatusPipeName, PipeDirection.InOut, first);
                first = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError("The status channel cannot be opened ({Message}); the tray shows the agent as stopped.", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                continue;
            }

            await using (server)
            {
                try
                {
                    await server.WaitForConnectionAsync(stoppingToken);
                    var line = JsonSerializer.Serialize(board.Snapshot(), AgentStatus.Json) + "\n";
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    await server.WriteAsync(Encoding.UTF8.GetBytes(line), timeout.Token);
                    await server.FlushAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    // the reader went away: the next one comes
                }
            }
        }
    }

    /// <summary>The snapshot of the service, or null when nobody answers (the service does not run) or the pipe is not the installed agent's.</summary>
    public static async Task<AgentStatus?> ReadAsync(TimeSpan timeout)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", AgentInfo.StatusPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var limit = new CancellationTokenSource(timeout);
            await pipe.ConnectAsync(limit.Token);
            if (!PipeTrust.ServerIsAgent(pipe))
            {
                return null;
            }

            using var reader = new StreamReader(pipe, new UTF8Encoding(false));
            var line = await reader.ReadLineAsync(limit.Token);
            return line is null ? null : JsonSerializer.Deserialize<AgentStatus>(line, AgentStatus.Json);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
