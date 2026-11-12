using MatHub.Data;
using MatHub.Services.Access;
using MatHub.Services.Clients;
using MatHub.Services.Jobs;
using MatHub.Services.Packages;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services.Deployments;

/// <summary>What the planner did in one round.</summary>
public sealed record PlanResult(int Jobs, int Runs);

/// <summary>
/// Makes the assignments of the packages true (desired state): every minute it looks at the members of each assigned group and makes the jobs that are missing:
/// an install where the package is not detected, an uninstall where it still is, an update where winget offers one (packages with automatic updates). A client
/// is left alone while a run of the assignment is open, for six hours after a failed one, and until it reported an inventory after a run that worked (so
/// nothing is installed twice). Without an inventory there is no detection: such a client waits for its first one.
/// </summary>
public static class Planner
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(6);

    public static async Task<PlanResult> RunOnceAsync(AppDbContext db, CancellationToken ct = default)
    {
        // the names of the jobs it makes are in the language of the first administrator (nobody is signed in here)
        var language = await db.Users.AsNoTracking().Where(u => u.Role == UserRole.Admin).OrderBy(u => u.Id).Select(u => u.Language).FirstOrDefaultAsync(ct);
        System.Globalization.CultureInfo.CurrentUICulture = Localization.Languages.UiCulture(string.IsNullOrEmpty(language) ? Localization.Languages.Default : language);
        await GroupService.RefreshAllAsync(db, ct);
        var assignments = await db.PackageAssignments.AsNoTracking().Include(a => a.Package).Where(a => a.IsActive && a.Package!.UpdateState != SoftEntity.Deleted).ToListAsync(ct);
        var jobs = 0;
        var runs = 0;
        foreach (var a in assignments)
        {
            var package = a.Package!;
            var now = DateTime.UtcNow;
            var retry = now - RetryAfter;
            var members = await db.Clients.AsNoTracking()
                .Where(c => c.Status == ClientStatus.Active && c.LastInventoryDate != null && db.ClientGroupMembers.Any(m => m.ClientId == c.Id && m.ClientGroupId == a.ClientGroupId))
                .Where(c => !db.JobRuns.Any(r => r.ClientId == c.Id && r.Job!.PackageAssignmentId == a.Id
                                                 && (r.Status == RunStatus.Queued || r.Status == RunStatus.Running
                                                     || (r.Status == RunStatus.Failed && r.FinishedDate > retry)
                                                     || (r.Status == RunStatus.Succeeded && r.FinishedDate > c.LastInventoryDate))))
                .Select(c => c.Id).ToListAsync(ct);
            if (members.Count == 0)
            {
                continue;
            }

            var detected = package.Detection == DetectionKind.None
                ? await db.JobRuns.Where(r => members.Contains(r.ClientId) && r.Job!.PackageAssignmentId == a.Id && r.Status == RunStatus.Succeeded && r.Job.Kind != JobKind.WingetUpgrade)
                    .Select(r => r.ClientId).Distinct().ToListAsync(ct)
                : await PackageService.InstalledQuery(db, package.Detection, package.DetectionValue, package.WingetId).Where(s => members.Contains(s.ClientId))
                    .Select(s => s.ClientId).Distinct().ToListAsync(ct);

            var install = a.Action == AssignmentAction.Install ? members.Except(detected).ToList() : [];
            var remove = a.Action == AssignmentAction.Uninstall ? members.Intersect(detected).ToList() : [];
            var update = new List<long>();
            if (a.Action == AssignmentAction.Install && package.Kind == PackageKind.Winget && package.AutoUpdate && package.WingetId is { } wingetId)
            {
                var id = wingetId.ToLower();
                update = await db.ClientSoftware.Where(s => detected.Contains(s.ClientId) && s.AvailableVersion != null && s.WingetId != null && s.WingetId.ToLower() == id)
                    .Select(s => s.ClientId).Distinct().ToListAsync(ct);
            }

            foreach (var (kind, clients) in new[] { (JobKind.PackageInstall, install), (JobKind.PackageUninstall, remove), (JobKind.WingetUpgrade, update) })
            {
                if (clients.Count == 0)
                {
                    continue;
                }

                var request = new JobRequest(kind, null, package.Id, kind == JobKind.WingetUpgrade ? package.WingetId : null, clients, []);
                var outcome = await JobService.CreateAsync(db, Actor.System, request, "planner", a.Id, ct);
                if (outcome.Ok)
                {
                    jobs++;
                    runs += clients.Count;
                }
            }
        }

        return new PlanResult(jobs, runs);
    }
}

/// <summary>Runs the <see cref="Planner"/> every minute.</summary>
public sealed class PlannerService(IServiceScopeFactory scopes, ILogger<PlannerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var result = await Planner.RunOnceAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), stoppingToken);
                    if (result.Jobs > 0)
                    {
                        logger.LogInformation("The planner made {Jobs} job(s) with {Runs} run(s) for the assignments.", result.Jobs, result.Runs);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "The planner failed; it tries again in a minute.");
                }

                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // the app stops
        }
    }
}
