using MatHub.Data;
using MatHub.Services.Access;
using MatHub.Services.Clients;
using MatHub.Services.Jobs;
using MatHub.Services.Packages;
using Microsoft.EntityFrameworkCore;
using PackageVersion = MatHub.Data.PackageVersion;

namespace MatHub.Services.Deployments;

/// <summary>What the planner did in one round.</summary>
public sealed record PlanResult(int Jobs, int Runs);

/// <summary>
/// Makes the assignments of the packages true (desired state): every minute it looks at the members of each assigned group and makes the jobs that are missing.
/// What it installs is the approved version of the package – for the members of its pilot group the version in test – where the package is missing or older;
/// it removes the package where an assignment says so. Nothing else: a version nobody approved never leaves MatHub. A client is left alone while a run of the
/// assignment is open, for six hours after a failed one, and until it reported an inventory after a run that worked (so nothing is installed twice). Without an
/// inventory there is no detection: such a client waits for its first one.
/// </summary>
public static class Planner
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(6);

    /// <summary>
    /// The language of the jobs MatHub makes by itself (nobody is signed in there): that of the first administrator. The caller sets it
    /// (<c>CultureInfo.CurrentUICulture = …</c>): a culture set inside an async method is gone again when it returns.
    /// </summary>
    public static async Task<System.Globalization.CultureInfo> AdminCultureAsync(AppDbContext db, CancellationToken ct = default)
    {
        var language = await db.Users.AsNoTracking().Where(u => u.Role == UserRole.Admin).OrderBy(u => u.Id).Select(u => u.Language).FirstOrDefaultAsync(ct);
        return Localization.Languages.UiCulture(string.IsNullOrEmpty(language) ? Localization.Languages.Default : language);
    }

    public static async Task<PlanResult> RunOnceAsync(AppDbContext db, CancellationToken ct = default)
    {
        System.Globalization.CultureInfo.CurrentUICulture = await AdminCultureAsync(db, ct);
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

            var (approved, testing) = await PackageVersions.CurrentAsync(db, package.Id, ct);
            if (approved is null && testing is null)
            {
                continue;
            }

            // the members of the pilot group get the version in test
            var pilots = testing is not null && package.PilotGroupId is { } pilotGroup
                ? await db.ClientGroupMembers.Where(m => m.ClientGroupId == pilotGroup && members.Contains(m.ClientId)).Select(m => m.ClientId).ToListAsync(ct)
                : [];
            PackageVersion? TargetOf(long client) => pilots.Contains(client) ? testing : approved;

            // the version each member has: by the detection, or (without one) by the runs of this assignment that worked
            var installed = new Dictionary<long, string?>();
            if (package.Detection == DetectionKind.None)
            {
                var done = await db.JobRuns.Where(r => members.Contains(r.ClientId) && r.Job!.PackageAssignmentId == a.Id && r.Status == RunStatus.Succeeded && r.Job.Kind == JobKind.PackageInstall)
                    .OrderBy(r => r.Id).Select(r => new { r.ClientId, r.Job!.PackageVersionId }).ToListAsync(ct);
                var versions = await db.PackageVersions.AsNoTracking().Where(v => v.PackageId == package.Id).ToDictionaryAsync(v => v.Id, v => v.Version, ct);
                foreach (var d in done)
                {
                    installed[d.ClientId] = d.PackageVersionId is { } vid && versions.TryGetValue(vid, out var v) ? v : "0";
                }
            }
            else
            {
                var rows = await PackageService.InstalledQuery(db, package.Detection, package.DetectionValue, package.WingetId).Where(s => members.Contains(s.ClientId))
                    .Select(s => new { s.ClientId, s.Version }).ToListAsync(ct);
                foreach (var g in rows.GroupBy(r => r.ClientId))
                {
                    installed[g.Key] = g.Select(r => r.Version ?? "0").OrderByDescending(v => v, Comparer<string>.Create(WingetCatalog.CompareVersions)).First();
                }
            }

            var work = new List<(JobKind Kind, PackageVersion Version, List<long> Clients)>();
            if (a.Action == AssignmentAction.Install)
            {
                // missing or older than its version: installed (an installer updates what is there)
                foreach (var group in members.Select(m => (Client: m, Target: TargetOf(m))).Where(x => x.Target is not null)
                    .Where(x => !installed.TryGetValue(x.Client, out var have) || have is null
                                || (PackageVersions.Comparable(x.Target!.Version) && WingetCatalog.CompareVersions(have, x.Target.Version) < 0))
                    .GroupBy(x => x.Target!.Id))
                {
                    work.Add((JobKind.PackageInstall, group.First().Target!, group.Select(x => x.Client).ToList()));
                }
            }
            else
            {
                var remove = members.Where(installed.ContainsKey).ToList();
                if (remove.Count > 0)
                {
                    work.Add((JobKind.PackageUninstall, (approved ?? testing)!, remove));
                }
            }

            foreach (var (kind, version, clients) in work)
            {
                var request = new JobRequest(kind, null, package.Id, null, clients, [], PackageVersionId: version.Id);
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
