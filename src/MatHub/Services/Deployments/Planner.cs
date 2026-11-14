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
/// Makes the assignments of the packages true (desired state): every minute it looks at the members of the assigned groups and makes the jobs that are missing.
/// It decides per package and client over all assignments of the package: a client in a group that removes the package has it removed (a removal wins over an
/// install), otherwise it gets the approved version – a member of the pilot group the version in test – where the package is missing or older. A version
/// nobody approved never leaves MatHub. A client is left alone while a run of the package is open, for six hours after a failed one, and until it reported an
/// inventory after a run that worked (so nothing is installed twice). It does not try again what a run that worked could not change: a version installed but
/// not found by the detection, a package removed but still found (<see cref="ClientPackage"/>) – a newer version is tried again. Without an inventory there
/// is no detection: such a client waits for its first one.
/// </summary>
public static class Planner
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(6);

    /// <summary>One round at a time (the service every minute, the action of the platform on demand).</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

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
        await Gate.WaitAsync(ct);
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = await AdminCultureAsync(db, ct);
            await GroupService.RefreshAllAsync(db, ct);
            var assignments = await db.PackageAssignments.AsNoTracking().Include(a => a.Package).Where(a => a.IsActive && a.Package!.UpdateState != SoftEntity.Deleted).ToListAsync(ct);
            var jobs = 0;
            var runs = 0;
            foreach (var byPackage in assignments.GroupBy(a => a.PackageId))
            {
                foreach (var (kind, version, assignment, clients) in await PlanPackageAsync(db, byPackage.First().Package!, byPackage.ToList(), ct))
                {
                    var request = new JobRequest(kind, null, version.PackageId, null, clients, [], PackageVersionId: version.Id);
                    var outcome = await JobService.CreateAsync(db, Actor.System, request, "planner", assignment, ct);
                    if (outcome.Ok)
                    {
                        jobs++;
                        runs += clients.Count;
                    }
                }
            }

            return new PlanResult(jobs, runs);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<List<(JobKind Kind, PackageVersion Version, long Assignment, List<long> Clients)>> PlanPackageAsync(
        AppDbContext db, Package package, List<PackageAssignment> assignments, CancellationToken ct)
    {
        var work = new List<(JobKind, PackageVersion, long, List<long>)>();
        var (approved, testing) = await PackageVersions.CurrentAsync(db, package.Id, ct);
        if (approved is null && testing is null && assignments.All(a => a.Action == AssignmentAction.Install))
        {
            return work;
        }

        // what each member should have: a removal wins over an install
        var groupIds = assignments.Select(a => a.ClientGroupId).Distinct().ToList();
        var membership = await db.ClientGroupMembers.AsNoTracking()
            .Where(m => groupIds.Contains(m.ClientGroupId) && m.Client!.Status == ClientStatus.Active && m.Client.LastInventoryDate != null)
            .Select(m => new { m.ClientId, m.ClientGroupId }).ToListAsync(ct);
        var desired = new Dictionary<long, PackageAssignment>();
        foreach (var m in membership)
        {
            var a = assignments.First(x => x.ClientGroupId == m.ClientGroupId);
            if (!desired.TryGetValue(m.ClientId, out var before) || (before.Action == AssignmentAction.Install && a.Action == AssignmentAction.Uninstall))
            {
                desired[m.ClientId] = a;
            }
        }

        // left alone: a run of the package is open, failed lately, or worked and the inventory after it has not come yet
        var ids = desired.Keys.ToList();
        var retry = DateTime.UtcNow - RetryAfter;
        var busy = await db.Clients.AsNoTracking()
            .Where(c => ids.Contains(c.Id) && db.JobRuns.Any(r => r.ClientId == c.Id && r.Job!.PackageId == package.Id
                && (r.Status == RunStatus.Queued || r.Status == RunStatus.Running
                    || (r.Status == RunStatus.Failed && r.FinishedDate > retry)
                    || (r.Status == RunStatus.Succeeded && r.FinishedDate > c.LastInventoryDate))))
            .Select(c => c.Id).ToListAsync(ct);
        var clients = ids.Except(busy).ToList();
        if (clients.Count == 0)
        {
            return work;
        }

        var states = await db.ClientPackages.AsNoTracking().Where(x => x.PackageId == package.Id && clients.Contains(x.ClientId)).ToDictionaryAsync(x => x.ClientId, ct);
        var versions = await db.PackageVersions.AsNoTracking().Include(v => v.Installers).Where(v => v.PackageId == package.Id).ToListAsync(ct);

        // the version each client has: by the detection, or (without one) by what MatHub did there last
        var installed = new Dictionary<long, string>();
        if (package.Detection == DetectionKind.None)
        {
            foreach (var (client, state) in states.Where(x => x.Value.Installed))
            {
                installed[client] = versions.FirstOrDefault(v => v.Id == state.PackageVersionId)?.Version ?? "0";
            }
        }
        else
        {
            var rows = await PackageService.InstalledQuery(db, package.Detection, package.DetectionValue, package.WingetId).Where(s => clients.Contains(s.ClientId))
                .Select(s => new { s.ClientId, s.Version }).ToListAsync(ct);
            foreach (var g in rows.GroupBy(r => r.ClientId))
            {
                installed[g.Key] = g.Select(r => r.Version ?? "0").OrderByDescending(v => v, Comparer<string>.Create(WingetCatalog.CompareVersions)).First();
            }
        }

        var pilots = testing is not null && package.PilotGroupId is { } pilotGroup
            ? (await db.ClientGroupMembers.AsNoTracking().Where(m => m.ClientGroupId == pilotGroup && clients.Contains(m.ClientId)).Select(m => m.ClientId).ToListAsync(ct)).ToHashSet()
            : [];
        var plan = new Dictionary<(JobKind Kind, long Version, long Assignment), (PackageVersion Version, List<long> Clients)>();
        void Add(JobKind kind, PackageVersion version, long assignment, long client)
        {
            if (!plan.TryGetValue((kind, version.Id, assignment), out var entry))
            {
                entry = (version, []);
                plan[(kind, version.Id, assignment)] = entry;
            }

            entry.Clients.Add(client);
        }

        foreach (var client in clients)
        {
            var assignment = desired[client];
            states.TryGetValue(client, out var state);
            var has = installed.TryGetValue(client, out var have);
            if (assignment.Action == AssignmentAction.Install)
            {
                var target = pilots.Contains(client) ? testing : approved;
                if (target is null || (has && !(PackageVersions.Comparable(target.Version) && WingetCatalog.CompareVersions(have!, target.Version) < 0)))
                {
                    continue;
                }

                // installed by a run that worked, yet not found (or older) in the inventory after it: no second try of the same version
                if (state is { Installed: true } && state.PackageVersionId == target.Id)
                {
                    continue;
                }

                Add(JobKind.PackageInstall, target, assignment.Id, client);
            }
            else if (has && state is not { Installed: false })
            {
                // removed with the installer of the version the client has (an MSI of another version has another product code)
                var version = versions.Where(v => v.Status != VersionStatus.New && v.Installers.Count > 0)
                                  .FirstOrDefault(v => string.Equals(v.Version, have, StringComparison.OrdinalIgnoreCase))
                              ?? versions.FirstOrDefault(v => v.Id == state?.PackageVersionId && v.Status != VersionStatus.New && v.Installers.Count > 0)
                              ?? approved ?? testing;
                if (version is not null)
                {
                    Add(JobKind.PackageUninstall, version, assignment.Id, client);
                }
            }
        }

        work.AddRange(plan.Select(p => (p.Key.Kind, p.Value.Version, p.Key.Assignment, p.Value.Clients)));
        return work;
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
