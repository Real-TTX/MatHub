using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MatHub.Protocol;

namespace MatHub.Agent;

/// <summary>Wakes the loop of the service before its time (the kiosk started something: the job is there at once).</summary>
public sealed class AgentSignal
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    public void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // already awake
        }
    }

    /// <summary>Waits the time, or until somebody wakes the loop.</summary>
    public Task WaitAsync(TimeSpan delay, CancellationToken ct) => _wake.WaitAsync(delay, ct);
}

/// <summary>A request on the pipe of the agent (one JSON line), and its answer (one JSON line).</summary>
public sealed record PipeRequest
{
    /// <summary>wait, answer, ask, kiosk, start, state, hello.</summary>
    public string Op { get; init; } = "hello";

    public long? Id { get; init; }

    public long? RunId { get; init; }

    public string? User { get; init; }

    public string? DialogId { get; init; }

    public UiDialog? Dialog { get; init; }

    /// <summary>ask: wait for the answer (false: notices and "please wait").</summary>
    public bool Wait { get; init; } = true;

    public UiReply? Reply { get; init; }
}

public sealed record PipeResponse
{
    public string? Error { get; init; }

    public string? Version { get; init; }

    public KioskOffer? Offer { get; init; }

    public long? RunId { get; init; }

    public RunState? State { get; init; }

    public string? DialogId { get; init; }

    public UiDialog? Dialog { get; init; }

    public UiReply? Reply { get; init; }
}

/// <summary>
/// The local door of the service: a named pipe the kiosk of the signed-in person and the scripts of the agent talk through. The kiosk asks for the offers and
/// starts them (the service asks the server: the person's process never sees the secret of the agent), waits for dialogs and answers them; a script that
/// shows a dialog (<c>mathub-agent ask</c>) hands it in and gets the answer. Every user of the computer may open the pipe; what it allows is what the kiosk
/// shows anyway. The service creates the pipe first (at the start of Windows), so nobody else can take its name.
/// </summary>
public sealed class UiBroker(AgentClient client, AgentSignal signal, ILogger<UiBroker> logger) : BackgroundService
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private sealed record Pending(string Id, UiDialog Dialog, TaskCompletionSource<UiReply> Done);

    private readonly Channel<Pending> _queue = Channel.CreateUnbounded<Pending>();
    private readonly ConcurrentDictionary<string, Pending> _shown = new();
    private long _lastUi;

    /// <summary>A kiosk asked for dialogs in the last minute: somebody is signed in and can answer.</summary>
    public bool UiAround => DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastUi) < TimeSpan.FromSeconds(60).Ticks;

    /// <summary>A job ended: its "please wait" window closes.</summary>
    public void JobEnded()
    {
        if (UiAround)
        {
            _queue.Writer.TryWrite(new Pending(Guid.NewGuid().ToString("N"), new UiDialog { Kind = "close" }, new TaskCompletionSource<UiReply>()));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = Create(first);
                first = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError("The pipe of the kiosk cannot be opened ({Message}); the kiosk and dialogs do not work until the agent restarts.", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync();
                break;
            }
            catch (IOException)
            {
                await server.DisposeAsync();
                continue;
            }

            _ = Task.Run(() => HandleAsync(server, stoppingToken), stoppingToken);
        }
    }

    /// <summary>The pipe: as SYSTEM (the service) every signed-in user may read and write it, the first instance makes sure the name is ours.</summary>
    private static NamedPipeServerStream Create(bool first)
    {
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        if (!WindowsIdentity.GetCurrent().IsSystem)
        {
            return new NamedPipeServerStream(AgentInfo.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, options);
        }

        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(AgentInfo.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, options, 0, 0, security);
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var _ = pipe;
        PipeResponse? response = null;
        try
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line is null || line.Length > 1_000_000)
            {
                return;
            }

            var request = JsonSerializer.Deserialize<PipeRequest>(line, Json) ?? new PipeRequest();
            response = await AnswerAsync(request, ct);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, Json).AsMemory(), ct);
        }
        catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException or ObjectDisposedException)
        {
            // the kiosk went away while it waited: a dialog it took goes back into the queue
            if (response?.DialogId is { } id && _shown.TryRemove(id, out var pending) && !pending.Done.Task.IsCompleted)
            {
                _queue.Writer.TryWrite(pending);
            }
        }
    }

    private async Task<PipeResponse> AnswerAsync(PipeRequest request, CancellationToken ct)
    {
        switch (request.Op)
        {
            case "wait":
                Interlocked.Exchange(ref _lastUi, DateTime.UtcNow.Ticks);
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    wait.CancelAfter(TimeSpan.FromSeconds(25));
                    try
                    {
                        while (true)
                        {
                            var pending = await _queue.Reader.ReadAsync(wait.Token);
                            if (pending.Done.Task.IsCompleted)
                            {
                                continue;
                            }

                            if (pending.Dialog.Kind is UiKinds.Message or UiKinds.Question or UiKinds.Form)
                            {
                                _shown[pending.Id] = pending;
                            }

                            return new PipeResponse { Version = AgentInfo.Version, DialogId = pending.Id, Dialog = pending.Dialog };
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        return new PipeResponse { Version = AgentInfo.Version };
                    }
                }

            case "answer":
                if (request.DialogId is { } answered && _shown.TryRemove(answered, out var shown))
                {
                    shown.Done.TrySetResult(request.Reply ?? new UiReply());
                }

                return new PipeResponse { Version = AgentInfo.Version };

            case "ask":
                return new PipeResponse { Reply = await AskAsync(request.Dialog ?? new UiDialog(), request.Wait, ct) };

            case "kiosk":
                try
                {
                    return new PipeResponse { Version = AgentInfo.Version, Offer = await client.KioskAsync(ct) };
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AgentRejectedException)
                {
                    return new PipeResponse { Version = AgentInfo.Version, Error = "offline" };
                }

            case "start":
                try
                {
                    var runId = await client.StartKioskAsync(request.Id ?? 0, request.User, ct);
                    signal.Wake();
                    logger.LogInformation("Kiosk: {User} started offer {Id} (run {RunId}).", request.User, request.Id, runId);
                    return new PipeResponse { RunId = runId };
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AgentRejectedException)
                {
                    return new PipeResponse { Error = "offline" };
                }

            case "state":
                try
                {
                    return new PipeResponse { State = await client.RunStateAsync(request.RunId ?? 0, ct) };
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AgentRejectedException)
                {
                    return new PipeResponse { Error = "offline" };
                }

            default:
                return new PipeResponse { Version = AgentInfo.Version };
        }
    }

    /// <summary>A dialog of a script: nobody signed in (no kiosk asked lately) answers "nobody"; a notice does not wait; else the answer, or "timeout".</summary>
    private async Task<UiReply> AskAsync(UiDialog dialog, bool wait, CancellationToken ct)
    {
        if (!UiAround)
        {
            return new UiReply { Answer = "nobody" };
        }

        var pending = new Pending(Guid.NewGuid().ToString("N"), dialog, new TaskCompletionSource<UiReply>(TaskCreationOptions.RunContinuationsAsynchronously));
        _queue.Writer.TryWrite(pending);
        if (!wait)
        {
            return new UiReply { Answer = "ok" };
        }

        var limit = TimeSpan.FromSeconds(Math.Clamp(dialog.TimeoutSeconds, 10, 86400) + 30);
        var done = await Task.WhenAny(pending.Done.Task, Task.Delay(limit, ct));
        if (done == pending.Done.Task)
        {
            return await pending.Done.Task;
        }

        _shown.TryRemove(pending.Id, out _);
        pending.Done.TrySetResult(new UiReply { Answer = "timeout" });
        return new UiReply { Answer = "timeout" };
    }
}

/// <summary>The other end of the pipe: the kiosk, the tray and <c>mathub-agent ask</c>.</summary>
public static class UiPipe
{
    public static async Task<PipeResponse> RequestAsync(PipeRequest request, TimeSpan timeout, CancellationToken ct = default)
    {
        await using var pipe = new NamedPipeClientStream(".", AgentInfo.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        await pipe.ConnectAsync(3000, limit.Token);
        if (!PipeTrust.ServerIsAgent(pipe))
        {
            throw new IOException("The pipe does not belong to the MatHub agent.");
        }

        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, UiBroker.Json).AsMemory(), limit.Token);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        var line = await reader.ReadLineAsync(limit.Token) ?? throw new IOException("The agent closed the pipe.");
        return JsonSerializer.Deserialize<PipeResponse>(line, UiBroker.Json) ?? new PipeResponse();
    }

    /// <summary>
    /// <c>mathub-agent ask --data &lt;base64 of the dialog JSON&gt; [--no-wait]</c>: what the PowerShell of a script calls; prints the answer as one JSON line
    /// (only ASCII, so the code page of the console does not matter). Without the agent the answer is "nobody".
    /// </summary>
    public static async Task<int> AskAsync(string? data, bool noWait)
    {
        UiReply reply;
        try
        {
            var dialog = JsonSerializer.Deserialize<UiDialog>(Encoding.UTF8.GetString(Convert.FromBase64String(data ?? string.Empty)), UiBroker.Json) ?? new UiDialog();
            var wait = TimeSpan.FromSeconds(noWait ? 15 : Math.Clamp(dialog.TimeoutSeconds, 10, 86400) + 60);
            var response = await RequestAsync(new PipeRequest { Op = "ask", Dialog = dialog, Wait = !noWait }, wait);
            reply = response.Reply ?? new UiReply { Answer = "nobody" };
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or FormatException or JsonException or UnauthorizedAccessException)
        {
            reply = new UiReply { Answer = "nobody" };
        }

        Console.WriteLine(JsonSerializer.Serialize(reply, UiBroker.Json));
        return 0;
    }
}

/// <summary>Whether the process at the other end of the pipe is the installed agent (a test as a normal user trusts its own pipe).</summary>
public static class PipeTrust
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static bool ServerIsAgent(NamedPipeClientStream pipe)
    {
        if (AgentInfo.OwnDataDirectory)
        {
            return true;
        }

        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid))
        {
            return false;
        }

        var process = OpenProcess(0x1000, false, pid);
        if (process == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var name = new StringBuilder(1024);
            var size = (uint)name.Capacity;
            return QueryFullProcessImageName(process, 0, name, ref size) && string.Equals(name.ToString(), AgentInfo.InstalledExe, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseHandle(process);
        }
    }
}
