using MatHub.Data;
using MatHub.Services.Access;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services.Packages;

/// <summary>One program with an update on some clients: what winget offers, on how many clients.</summary>
public sealed record UpdateRow(string WingetId, string Name, string? Installed, string Available, int Clients);

public sealed record UpdateList(IReadOnlyList<UpdateRow> Items, int Programs, int Clients);

/// <summary>A version that waits for its approval (new) or is in test.</summary>
public sealed record PendingRow(long PackageId, string Package, long VersionId, string Version, VersionStatus Status, string Source, DateTime Created, string? Approved, string? Pilot);

/// <summary>A package whose catalog has a newer version than any of the package (not prepared: the package does not prepare by itself, or it failed).</summary>
public sealed record CatalogRow2(long PackageId, string Package, string? Approved, string Catalog);

/// <summary>A package with clients that have an older version than the approved one (the planner updates the assigned ones).</summary>
public sealed record OutdatedRow(long PackageId, string Package, string Approved, int Outdated, int Current);

public sealed record UpdateOverview(IReadOnlyList<PendingRow> Pending, IReadOnlyList<CatalogRow2> InCatalog, IReadOnlyList<OutdatedRow> Outdated, UpdateList Unmanaged);

/// <summary>
/// Updates are versions: MatHub finds new ones (in the catalog of winget, or a person uploads them), a person tests and approves them, the planner brings the
/// approved version to the assigned clients. This page of the business layer says what waits: versions to test or approve, versions of the catalog nobody took
/// yet, clients that still have an older version. What winget would update on the clients beyond the packages is only shown (<see cref="ListAsync"/>): MatHub
/// installs nothing that was not approved.
/// </summary>
public static class UpdateService
{
    /// <summary>The updates winget offers on the clients for programs that are no package of MatHub (from their last inventory), program by program.</summary>
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
        var managed = db.Packages.Where(p => p.WingetId != null).Select(p => p.WingetId!.ToLower());
        var q = db.ClientSoftware.AsNoTracking().Where(s => s.AvailableVersion != null && s.WingetId != null && ids.Contains(s.ClientId) && !managed.Contains(s.WingetId.ToLower()));
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

    /// <summary>How many versions wait for a test or an approval (the badge of the menu).</summary>
    public static async Task<int> PendingCountAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
        => actor.Can(Permission.PackagesView) ? await db.PackageVersions.CountAsync(v => v.Status == VersionStatus.New && v.Package!.UpdateState != SoftEntity.Deleted, ct) : 0;

    public static async Task<UpdateOverview?> OverviewAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.PackagesView))
        {
            return null;
        }

        var packages = await db.Packages.AsNoTracking().Include(p => p.PilotGroup).ToListAsync(ct);
        var versions = await db.PackageVersions.AsNoTracking().Where(v => v.Status == VersionStatus.New || v.Status == VersionStatus.Testing).ToListAsync(ct);
        var byId = packages.ToDictionary(p => p.Id);
        var pending = versions.Where(v => byId.ContainsKey(v.PackageId))
            .Select(v => new PendingRow(v.PackageId, byId[v.PackageId].Name, v.Id, v.Version, v.Status, v.Source, v.CreateDate, byId[v.PackageId].Version, byId[v.PackageId].PilotGroup?.Name))
            .OrderBy(r => r.Status == VersionStatus.Testing ? 0 : 1).ThenBy(r => r.Package).ToList();

        var inCatalog = new List<CatalogRow2>();
        var outdated = new List<OutdatedRow>();
        foreach (var p in packages)
        {
            if (await WingetCatalog.NewerAsync(db, p, ct) is { } newer)
            {
                inCatalog.Add(new CatalogRow2(p.Id, p.Name, p.Version, newer));
            }

            if (p.Version is { } approved && PackageVersions.Comparable(approved) && actor.Can(Permission.ClientsView))
            {
                var installed = await PackageVersions.InstalledAsync(db, actor, p, ct);
                var older = installed.Count(i => i.Value is { } have && WingetCatalog.CompareVersions(have, approved) < 0);
                if (older > 0)
                {
                    outdated.Add(new OutdatedRow(p.Id, p.Name, approved, older, installed.Count - older));
                }
            }
        }

        var unmanaged = await ListAsync(db, actor, null, null, ct) ?? new UpdateList([], 0, 0);
        return new UpdateOverview(pending, inCatalog.OrderBy(r => r.Package).ToList(), outdated.OrderByDescending(r => r.Outdated).ToList(), unmanaged);
    }
}
