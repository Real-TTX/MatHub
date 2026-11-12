using MatHub.Data;
using MatHub.Services.Access;
using MatHub.Services.Jobs;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services.Packages;

/// <summary>One program with an update on some clients: what winget offers, on how many clients.</summary>
public sealed record UpdateRow(string WingetId, string Name, string? Installed, string Available, int Clients);

public sealed record UpdateList(IReadOnlyList<UpdateRow> Items, int Programs, int Clients);

/// <summary>
/// The updates winget offers on the clients (from their last inventory), program by program, and the jobs that install them: one program everywhere, or
/// everything on some clients. Making updates easy is the point: a click, and the job runs on every client that has the update.
/// </summary>
public static class UpdateService
{
    public static async Task<UpdateList?> ListAsync(AppDbContext db, Actor actor, string? search, long? groupId, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.PackagesView) || !actor.Can(Permission.ClientsView))
        {
            return null;
        }

        var clients = AccessRules.Clients(db, actor).Where(c => c.Status == ClientStatus.Active);
        if (groupId is { } g)
        {
            clients = clients.Where(c => db.ClientGroupMembers.Any(m => m.ClientId == c.Id && m.ClientGroupId == g));
        }

        var ids = clients.Select(c => c.Id);
        var q = db.ClientSoftware.AsNoTracking().Where(s => s.AvailableVersion != null && s.WingetId != null && ids.Contains(s.ClientId));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = $"%{search.Trim()}%";
            q = q.Where(x => EF.Functions.ILike(x.Name, s) || EF.Functions.ILike(x.WingetId!, s));
        }

        var rows = await q.Select(s => new { s.WingetId, s.Name, s.Version, s.AvailableVersion, s.ClientId }).ToListAsync(ct);
        var items = rows.GroupBy(r => r.WingetId!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new UpdateRow(g.Key, g.First().Name, g.Select(x => x.Version).Where(v => v is not null).Order().FirstOrDefault(), g.Max(x => x.AvailableVersion)!, g.Select(x => x.ClientId).Distinct().Count()))
            .OrderByDescending(r => r.Clients).ThenBy(r => r.Name)
            .ToList();
        return new UpdateList(items, items.Count, rows.Select(r => r.ClientId).Distinct().Count());
    }

    /// <summary>Updates one program (<paramref name="wingetId"/>) on every client the actor reaches that has the update, or everything (null) on those clients.</summary>
    public static async Task<Outcome<Job>> UpdateAsync(AppDbContext db, Actor actor, string? wingetId, long? groupId, IReadOnlyList<long>? clientIds = null, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.PackagesDeploy))
        {
            return Outcome<Job>.Denied();
        }

        var clients = AccessRules.Clients(db, actor).Where(c => c.Status == ClientStatus.Active);
        if (groupId is { } g)
        {
            clients = clients.Where(c => db.ClientGroupMembers.Any(m => m.ClientId == c.Id && m.ClientGroupId == g));
        }

        if (clientIds is { Count: > 0 })
        {
            var only = clientIds.ToList();
            clients = clients.Where(c => only.Contains(c.Id));
        }

        var ids = clients.Select(c => c.Id);
        var id = wingetId?.Trim().ToLower();
        var targets = await db.ClientSoftware
            .Where(s => s.AvailableVersion != null && s.WingetId != null && ids.Contains(s.ClientId) && (id == null || s.WingetId.ToLower() == id))
            .Select(s => s.ClientId).Distinct().ToListAsync(ct);
        if (targets.Count == 0)
        {
            return Outcome<Job>.Fail(Localization.Text.T("Updates.None"));
        }

        var result = await JobService.CreateAsync(db, actor, new JobRequest(JobKind.WingetUpgrade, null, null, wingetId, targets, []), ct: ct);
        return result;
    }
}
