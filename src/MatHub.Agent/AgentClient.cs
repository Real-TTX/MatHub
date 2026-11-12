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

    /// <summary>Downloads a file of the server (an installer, the next agent) to <paramref name="target"/>.</summary>
    public async Task DownloadAsync(string url, string target, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url.TrimStart('/'), HttpCompletionOption.ResponseHeadersRead, ct);
        Check(response);
        await using var file = File.Create(target);
        await response.Content.CopyToAsync(file, ct);
    }

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
