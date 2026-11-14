using MatHub.Data;
using MatHub.Protocol;
using MatHub.Services.Clients;
using MatHub.Services.Jobs;
using MatHub.Services.Kiosk;
using Microsoft.AspNetCore.DataProtection;
using MatHub.Services.Packages;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services.Agent;

/// <summary>
/// The API of the agents (<c>/api/agent/v1</c>, the protocol in <c>MatHub.Shared/AgentProtocol.cs</c>) and the downloads of the agent (<c>/agent/…</c>). The
/// agents do not use the cookie: an agent names its client (<c>X-MatHub-Client</c>) and proves it with its secret (<c>X-MatHub-Key</c>, compared with the hash
/// in constant time). A deleted or unknown client gets 401, a disabled one 403: the agent then stops asking for work.
/// </summary>
public static class AgentEndpoints
{
    public static void MapAgentEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/agent/v1").AllowAnonymous().DisableAntiforgery();

        api.MapPost("/enroll", async (EnrollRequest request, AppDbContext db, ILoggerFactory logs, HttpContext http, CancellationToken ct) =>
        {
            var outcome = await EnrollmentService.EnrollAsync(db, request, ct);
            if (outcome.Response is null)
            {
                logs.CreateLogger("MatHub.Agent").LogWarning("Enrollment of {Host} from {Address} refused: {Reason}", request.HostName, http.Connection.RemoteIpAddress, outcome.Error);
                return Results.StatusCode(outcome.Status);
            }

            return Results.Ok(outcome.Response);
        }).RequireRateLimiting("enroll");

        api.MapPost("/poll", async (PollRequest request, HttpContext http, AppDbContext db, AgentFiles files, IDataProtectionProvider protection, CancellationToken ct) =>
        {
            var (client, denied) = await AuthenticateAsync(http, db, ct);
            if (client is null)
            {
                return denied!;
            }

            var now = DateTime.UtcNow;
            client.LastSeenDate = now;
            client.AgentVersion = Trim(request.AgentVersion, 60) ?? client.AgentVersion;
            client.LastUser = Trim(request.LastUser, 200) ?? client.LastUser;
            client.BootDate = request.BootTime?.ToUniversalTime() ?? client.BootDate;
            var settings = await SettingsStore.LoadAsync(db, ct);
            ClientService.PollSeconds = settings.AgentPollSeconds;
            await db.SaveChangesAsync(ct);

            if (client.Status == ClientStatus.Pending)
            {
                return Results.Ok(new PollResponse { Status = "pending", PollSeconds = Math.Max(60, settings.AgentPollSeconds) });
            }

            var inventoryDue = client.InventoryRequested || client.LastInventoryDate is null || client.LastInventoryDate < now.AddHours(-settings.InventoryHours);
            var jobs = await JobService.TakeAsync(db, client.Id, 1, ct, protection);
            return Results.Ok(new PollResponse
            {
                Status = "active",
                // somebody tests a script on this computer: it asks every few seconds for a while
                PollSeconds = await db.Jobs.AnyAsync(j => (j.Origin == "test" || j.Origin == "trial") && j.CreateDate > now.AddMinutes(-15) && j.Runs.Any(r => r.ClientId == client.Id), ct)
                    ? Math.Min(5, settings.AgentPollSeconds)
                    : settings.AgentPollSeconds,
                InventoryDue = inventoryDue,
                Jobs = jobs,
                Agent = files.Release(),
            });
        });

        api.MapPost("/runs/{runId:long}/result", async (long runId, RunResult result, HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            var (client, denied) = await AuthenticateAsync(http, db, ct);
            if (client is null)
            {
                return denied!;
            }

            return await JobService.CompleteAsync(db, client.Id, runId, result, ct) ? Results.NoContent() : Results.NotFound();
        });

        api.MapPost("/inventory", async (InventoryReport report, HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            var (client, denied) = await AuthenticateAsync(http, db, ct);
            if (client is null)
            {
                return denied!;
            }

            await InventoryService.ApplyAsync(db, client, report, ct);
            return Results.NoContent();
        });

        // the installer of a package, only for an agent that has a run of it (also as the step of a sequence)
        api.MapGet("/installers/{id:long}/file", async (long id, HttpContext http, AppDbContext db, PackageFiles store, CancellationToken ct) =>
        {
            var (client, denied) = await AuthenticateAsync(http, db, ct);
            if (client is null)
            {
                return denied!;
            }

            var installer = await db.PackageInstallers.AsNoTracking().Include(i => i.PackageVersion).FirstOrDefaultAsync(i => i.Id == id, ct);
            if (installer?.FileName is null || !await JobService.PackageAllowedAsync(db, client.Id, installer.PackageVersion!.PackageId, ct)
                || store.Find(installer.PackageVersion.PackageId, installer.Id, installer.FileName) is not { } path)
            {
                return Results.NotFound();
            }

            return Results.File(path, "application/octet-stream", installer.FileName, enableRangeProcessing: true);
        });

        // the kiosk: the offers for this client (asked when the kiosk opens, so always the current ones), a start, the state of what it started
        api.MapGet("/kiosk", async (HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            var (client, denied) = await AuthenticateAsync(http, db, ct);
            if (client is null)
            {
                return denied!;
            }

            return client.Status == ClientStatus.Active ? Results.Ok(await KioskService.OfferAsync(db, client, ct)) : Results.Ok(new KioskOffer());
        });

        api.MapPost("/kiosk/{id:long}/start", async (long id, KioskStart request, HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            var (client, denied) = await AuthenticateAsync(http, db, ct);
            if (client is null)
            {
                return denied!;
            }

            var started = await KioskService.StartAsync(db, client, id, request.User, ct);
            return started.Ok ? Results.Ok(new KioskStarted { RunId = started.Value }) : started.NotAllowed ? Results.NotFound() : Results.Problem(started.Error, statusCode: 409);
        });

        // a file of a script, only for an agent that runs the script now
        api.MapGet("/files/{id:long}", async (long id, HttpContext http, AppDbContext db, MatHub.Services.Scripts.ScriptFileStore store, CancellationToken ct) =>
        {
            var (client, denied) = await AuthenticateAsync(http, db, ct);
            if (client is null)
            {
                return denied!;
            }

            var file = await db.ScriptFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);
            if (file is null || !await MatHub.Services.Scripts.ScriptFileService.AllowedAsync(db, client.Id, file, ct) || !File.Exists(store.PathOf(file.ScriptId, file.Name)))
            {
                return Results.NotFound();
            }

            return Results.File(store.PathOf(file.ScriptId, file.Name), "application/octet-stream", file.Name, enableRangeProcessing: true);
        });

        api.MapGet("/runs/{runId:long}", async (long runId, HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            var (client, denied) = await AuthenticateAsync(http, db, ct);
            if (client is null)
            {
                return denied!;
            }

            return await JobService.StateAsync(db, client.Id, runId, ct) is { } state ? Results.Ok(state) : Results.NotFound();
        });

        // the agent itself and its install script (anonymous: the agent is no secret, the enrollment key is)
        app.MapGet("/agent/" + AgentFiles.FileName, (AgentFiles files) =>
            files.Available ? Results.File(files.Path, "application/octet-stream", AgentFiles.FileName, enableRangeProcessing: true) : Results.NotFound()).AllowAnonymous();

        app.MapGet("/agent/install.ps1", async (HttpContext http, AgentFiles files, InstanceService instance) =>
        {
            var server = await instance.BaseUrlAsync(http.Request);
            return Results.Text(files.InstallScript(server), "text/plain; charset=utf-8");
        }).AllowAnonymous();
    }

    /// <summary>The client of the headers, when its secret fits and it is not disabled; else the answer to give.</summary>
    private static async Task<(Client? Client, IResult? Denied)> AuthenticateAsync(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (!Guid.TryParse(http.Request.Headers[AgentHeaders.Client].ToString(), out var token))
        {
            return (null, Results.Unauthorized());
        }

        var client = await db.Clients.FirstOrDefaultAsync(c => c.Token == token, ct);
        if (client is null || !EnrollmentService.Verify(http.Request.Headers[AgentHeaders.Key].ToString(), client.SecretHash))
        {
            return (null, Results.Unauthorized());
        }

        return client.Status == ClientStatus.Disabled ? (null, Results.StatusCode(StatusCodes.Status403Forbidden)) : (client, null);
    }

    private static string? Trim(string? text, int max)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > max ? t[..max] : t;
    }
}
