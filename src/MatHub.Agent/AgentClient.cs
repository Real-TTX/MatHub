using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MatHub.Protocol;

namespace MatHub.Agent;

/// <summary>The answer of the server was "this agent is not known" (401) or "switched off" (403).</summary>
public sealed class AgentRejectedException(HttpStatusCode status) : Exception($"The server refused the agent ({(int)status}).")
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>The calls of the agent to its server (the protocol in AgentProtocol.cs). Every call carries the token of the client and its secret.</summary>
public sealed class AgentClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public AgentClient(AgentConfig config)
    {
        _http = new HttpClient { BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(100) };
        _http.DefaultRequestHeaders.Add(AgentHeaders.Client, config.ClientToken);
        _http.DefaultRequestHeaders.Add(AgentHeaders.Key, config.Secret);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MatHub-Agent/" + Uri.EscapeDataString(AgentInfo.Version));
    }

    /// <summary>Enrolls this computer with a key (or the token of a rollout); answers the identity of the agent.</summary>
    public static async Task<EnrollResponse> EnrollAsync(string server, EnrollRequest request, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(server.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(60) };
        using var response = await http.PostAsJsonAsync("api/agent/v1/enroll", request, Json, ct);
        if (response.StatusCode is HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("The server refused the key (unknown, used up, ended or switched off).");
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<EnrollResponse>(Json, ct) ?? throw new InvalidOperationException("The server sent no answer.");
    }

    public async Task<PollResponse> PollAsync(PollRequest request, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("api/agent/v1/poll", request, Json, ct);
        Check(response);
        return await response.Content.ReadFromJsonAsync<PollResponse>(Json, ct) ?? new PollResponse();
    }

    public async Task ReportAsync(long runId, RunResult result, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync($"api/agent/v1/runs/{runId}/result", result, Json, ct);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            Check(response);
        }
    }

    public async Task SendInventoryAsync(InventoryReport report, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("api/agent/v1/inventory", report, Json, ct);
        Check(response);
    }

    /// <summary>The offers of the kiosk for this computer, as the server has them now.</summary>
    public async Task<KioskOffer> KioskAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/agent/v1/kiosk", ct);
        Check(response);
        return await response.Content.ReadFromJsonAsync<KioskOffer>(Json, ct) ?? new KioskOffer();
    }

    /// <summary>Starts an offer of the kiosk on this computer; answers the run (0: not offered here any more).</summary>
    public async Task<long> StartKioskAsync(long id, string? user, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync($"api/agent/v1/kiosk/{id}/start", new KioskStart { User = user }, Json, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            return 0;
        }

        Check(response);
        return (await response.Content.ReadFromJsonAsync<KioskStarted>(Json, ct))?.RunId ?? 0;
    }

    /// <summary>The state of a run of this computer (null: not known).</summary>
    public async Task<RunState?> RunStateAsync(long runId, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"api/agent/v1/runs/{runId}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        Check(response);
        return await response.Content.ReadFromJsonAsync<RunState>(Json, ct);
    }

    /// <summary>
    /// Downloads a file of the server (an installer, the next agent) to <paramref name="target"/>. Only from the server itself (the key of the agent goes with
    /// every call), and a connection that stops sending for two minutes ends the download (a laptop that slept or changed its network) instead of hanging.
    /// </summary>
    public async Task DownloadAsync(string url, string target, CancellationToken ct)
    {
        var uri = new Uri(_http.BaseAddress!, url.TrimStart('/'));
        if (Uri.Compare(uri, _http.BaseAddress, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)
        {
            throw new InvalidOperationException("A download has to come from the server of the agent.");
        }

        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(IdleLimit);
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, idle.Token);
        Check(response);
        await using var source = await response.Content.ReadAsStreamAsync(idle.Token);
        await using var file = File.Create(target);
        var buffer = new byte[81920];
        while (true)
        {
            idle.CancelAfter(IdleLimit);
            int read;
            try
            {
                read = await source.ReadAsync(buffer, idle.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException("The download stopped: the server sent nothing for two minutes.");
            }

            if (read == 0)
            {
                break;
            }

            await file.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(2);

    private static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AgentRejectedException(response.StatusCode);
        }

        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
}
