using MatHub.Data;
using MatHub.Services.Clients;
using MatHub.Services.Jobs;
using MatPlatform;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services;

/// <summary>The numbers of the installation (the start page, the platform tiles, the admin page System).</summary>
public sealed record InstanceSnapshot(
    int Users,
    int Admins,
    int Clients,
    int Online,
    int Pending,
    int ClientsWithUpdates,
    int Scripts,
    int Packages,
    int OpenRuns,
    int FailedRuns24h,
    int RolloutsWaiting,
    DateTime? LastActivityUtc);

/// <summary>
/// The settings of the system (admin page System and the platform contract). <see cref="PublicUrl"/> is the address in force: the value of <c>MAT_PUBLIC_URL</c>
/// while it is set (then <see cref="PublicUrlFixed"/>), else the stored one.
/// </summary>
public sealed record HubSettings(bool RegistrationOpen, string? PublicUrl, int AgentPollSeconds, int InventoryHours, int JobDays, string? BootUrl, bool PublicUrlFixed = false);

/// <summary>What to change of the settings; a <c>null</c> leaves a value as it is, an empty address clears it.</summary>
public sealed record HubSettingsChange(bool? RegistrationOpen = null, string? PublicUrl = null, int? AgentPollSeconds = null, int? InventoryHours = null, int? JobDays = null, string? BootUrl = null);

/// <summary>What the housekeeping removed.</summary>
public sealed record CleanUpResult(int Sessions, int Jobs, int LostRuns, int Rollouts);

/// <summary>
/// The installation as a whole: its numbers, the settings of the system, the state of the database and the housekeeping. The admin page System, the start page,
/// the platform contract (<c>Platform/MatHubPlatform</c>) and the background housekeeping go through it. A broker such as Mattiverse may set the public address
/// (<c>MAT_PUBLIC_URL</c>): then it wins and the stored value stays as it is (it comes back when the variable goes).
/// </summary>
public sealed class InstanceService(AppDbContext db, AccountService accounts, MatPlatformSettings platform, IConfiguration configuration)
{
    /// <summary>The public address in force: <c>MAT_PUBLIC_URL</c>, else the stored one; null = the address of the request.</summary>
    public static string? PublicUrlOf(SystemSetting s, MatPlatformSettings platform) => platform.AppPublicUrl ?? NormalizePublicUrl(s.PublicUrl);

    /// <summary>Can the database be reached (the health check)?</summary>
    public async Task<bool> CanConnectAsync(CancellationToken ct = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Can the data folder be written (installers, boot images)?</summary>
    public bool CanWriteFiles()
    {
        try
        {
            var dir = configuration["DataDirectory"] ?? "/data";
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<InstanceSnapshot> SnapshotAsync(CancellationToken ct = default)
    {
        var since = DateTime.UtcNow - ClientService.OnlineWindow;
        var dayAgo = DateTime.UtcNow.AddDays(-1);
        return new InstanceSnapshot(
            Users: await db.Users.CountAsync(u => u.IsActive, ct),
            Admins: await db.Users.CountAsync(u => u.IsActive && u.Role == UserRole.Admin, ct),
            Clients: await db.Clients.CountAsync(c => c.Status == ClientStatus.Active, ct),
            Online: await db.Clients.CountAsync(c => c.Status == ClientStatus.Active && c.LastSeenDate > since, ct),
            Pending: await db.Clients.CountAsync(c => c.Status == ClientStatus.Pending, ct),
            ClientsWithUpdates: await db.Clients.CountAsync(c => c.Status == ClientStatus.Active && c.UpdatesAvailable > 0, ct),
            Scripts: await db.Scripts.CountAsync(ct),
            Packages: await db.Packages.CountAsync(ct),
            OpenRuns: await db.JobRuns.CountAsync(r => r.Status == RunStatus.Queued || r.Status == RunStatus.Running, ct),
            FailedRuns24h: await db.JobRuns.CountAsync(r => r.Status == RunStatus.Failed && r.FinishedDate > dayAgo, ct),
            RolloutsWaiting: await db.RolloutTasks.CountAsync(t => t.Status == RolloutStatus.Waiting || t.Status == RolloutStatus.Installing, ct),
            LastActivityUtc: await db.Activities.MaxAsync(a => (DateTime?)a.OccurredDate, ct));
    }

    public Task<bool> AnyUserAsync() => accounts.AnyUserAsync();

    /// <summary>Where the data is: <c>PostgreSQL host:port/database</c> (for the admin page; never in the platform API).</summary>
    public string DatabaseName()
    {
        var cs = new Npgsql.NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
        return $"PostgreSQL {cs.Host}:{cs.Port}/{cs.Database}";
    }

    /// <summary>Whether sign-up is open and the public address in force (null = the address of the request).</summary>
    public async Task<(bool RegistrationOpen, string? PublicUrl)> SettingsAsync()
    {
        var s = await accounts.SettingsAsync();
        return (s.RegistrationOpen, PublicUrlOf(s, platform));
    }

    public async Task<HubSettings> HubSettingsAsync()
    {
        var s = await accounts.SettingsAsync();
        return new HubSettings(s.RegistrationOpen, PublicUrlOf(s, platform), s.AgentPollSeconds, s.InventoryHours, s.JobDays, NormalizePublicUrl(s.BootUrl),
            platform.AppPublicUrl is not null);
    }

    /// <summary>
    /// Saves the settings of the system. An address that a variable sets (<c>MAT_PUBLIC_URL</c>) is left as it is. Answers the field and the message of the first
    /// wrong value (nothing is saved then), else <c>null</c>.
    /// </summary>
    public async Task<(string Field, string Message)?> SaveSettingsAsync(HubSettingsChange change)
    {
        var publicUrl = platform.AppPublicUrl is null ? change.PublicUrl : null;
        if (publicUrl is not null && NormalizePublicUrl(publicUrl) is { } p && !IsPublicUrl(p))
        {
            return ("PublicUrl", Localization.Text.T("System.PublicUrl.Invalid"));
        }

        if (change.BootUrl is not null && NormalizePublicUrl(change.BootUrl) is { } b && !IsPublicUrl(b))
        {
            return ("BootUrl", Localization.Text.T("System.PublicUrl.Invalid"));
        }

        if (change.AgentPollSeconds is < 10 or > 3600)
        {
            return ("AgentPollSeconds", Localization.Text.T("System.Range", 10, 3600));
        }

        if (change.InventoryHours is < 1 or > 168)
        {
            return ("InventoryHours", Localization.Text.T("System.Range", 1, 168));
        }

        if (change.JobDays is < 1 or > 3650)
        {
            return ("JobDays", Localization.Text.T("System.Range", 1, 3650));
        }

        var s = await SettingsStore.LoadAsync(db);
        if (change.RegistrationOpen is { } open)
        {
            s.RegistrationOpen = open;
        }

        if (publicUrl is not null)
        {
            s.PublicUrl = NormalizePublicUrl(publicUrl);
        }

        if (change.BootUrl is not null)
        {
            s.BootUrl = NormalizePublicUrl(change.BootUrl);
        }

        s.AgentPollSeconds = change.AgentPollSeconds ?? s.AgentPollSeconds;
        s.InventoryHours = change.InventoryHours ?? s.InventoryHours;
        s.JobDays = change.JobDays ?? s.JobDays;
        await db.SaveChangesAsync();
        return null;
    }

    /// <summary>The address without spaces and the last slash; <c>null</c> when empty.</summary>
    public static string? NormalizePublicUrl(string? url)
    {
        var trimmed = (url ?? string.Empty).Trim().TrimEnd('/');
        return trimmed.Length == 0 ? null : trimmed;
    }

    public static bool IsPublicUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    /// <summary>The address MatHub is reached at for this request: the public address in force, else scheme and host of the request.</summary>
    public async Task<string> BaseUrlAsync(HttpRequest request)
        => (await SettingsAsync()).PublicUrl ?? $"{request.Scheme}://{request.Host}";

    /// <summary>
    /// The housekeeping: ended sessions go; runs that never came back fail; jobs older than the days of the settings go with their runs; finished rollout tasks
    /// older than 30 days go.
    /// </summary>
    public async Task<CleanUpResult> CleanUpAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var settings = await accounts.SettingsAsync();
        var sessions = await db.Sessions.Where(s => s.IsRevoked || s.ExpiresDate < now.AddDays(-1)).ExecuteDeleteAsync(ct);
        var lost = await JobService.FailLostRunsAsync(db, ct);
        var limit = now.AddDays(-Math.Max(1, settings.JobDays));
        var jobs = await db.Jobs
            .Where(j => j.CreateDate < limit && !db.JobRuns.Any(r => r.JobId == j.Id && (r.Status == RunStatus.Queued || r.Status == RunStatus.Running)))
            .ExecuteDeleteAsync(ct);
        var rolloutLimit = now.AddDays(-30);
        var rollouts = await db.RolloutTasks
            .Where(t => (t.Status == RolloutStatus.Completed || t.Status == RolloutStatus.Cancelled) && t.UpdateDate < rolloutLimit)
            .ExecuteDeleteAsync(ct);
        return new CleanUpResult(sessions, jobs, lost, rollouts);
    }
}
