using MatHub.Data;
using MatHub.Services.Access;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services.Clients;

/// <summary>What the list of clients shows; <paramref name="Update"/>: only the clients winget offers an update of this program (its id) on.</summary>
public sealed record ClientCriteria(string? Search, string? State, long? GroupId, string? System, string Sort, bool Descending, int Page, int PageSize, string? Update = null);

public sealed record ClientRow(
    long Id, string Name, ClientStatus Status, bool Online, string? OperatingSystem, string? IpAddresses, string? LastUser, DateTime? LastSeen, int Updates, string? AgentVersion);

public sealed record ClientList(IReadOnlyList<ClientRow> Items, int Total, int Matching);

public sealed record ClientGroupRef(long Id, string Name, string? Colour, GroupKind Kind);

public sealed record ClientDetail(Client Client, bool Online, IReadOnlyList<ClientGroupRef> Groups);

/// <summary>What a person may change of a client: the description, the tags and the static groups.</summary>
public sealed record ClientForm(string? Description, string? Tags, IReadOnlyList<long> GroupIds);

/// <summary>The clients an actor reaches in numbers: by state and by operating system (the overview).</summary>
public sealed record ClientOverview(int Total, int Online, int Offline, int Pending, int Disabled, int WithUpdates, IReadOnlyList<(string System, int Count)> Systems);

public sealed record SoftwareRow(string Name, string? Version, string? Publisher, string? WingetId, string? AvailableVersion);

/// <summary>
/// The business layer of the clients (static, the permission check in every function). A client the actor does not reach is "not found" (null / Denied), exactly
/// like a client that is not there.
/// </summary>
public static class ClientService
{
    /// <summary>A client is online while its agent called within this time.</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(3);

    public static bool IsOnline(DateTime? lastSeen) => lastSeen is { } seen && DateTime.UtcNow - seen < OnlineWindow;

    public static async Task<ClientList?> ListAsync(AppDbContext db, Actor actor, ClientCriteria c, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ClientsView))
        {
            return null;
        }

        var all = AccessRules.Clients(db, actor).AsNoTracking();
        var total = await all.CountAsync(ct);
        var q = all;
        if (!string.IsNullOrWhiteSpace(c.Search))
        {
            var s = $"%{c.Search.Trim()}%";
            q = q.Where(x => EF.Functions.ILike(x.Name, s) || EF.Functions.ILike(x.IpAddresses ?? string.Empty, s) || EF.Functions.ILike(x.LastUser ?? string.Empty, s)
                             || EF.Functions.ILike(x.SerialNumber ?? string.Empty, s) || EF.Functions.ILike(x.Tags, s) || EF.Functions.ILike(x.Description ?? string.Empty, s)
                             || EF.Functions.ILike(x.MacAddresses ?? string.Empty, s));
        }

        var since = DateTime.UtcNow - OnlineWindow;
        q = c.State switch
        {
            "online" => q.Where(x => x.Status == ClientStatus.Active && x.LastSeenDate > since),
            "offline" => q.Where(x => x.Status == ClientStatus.Active && (x.LastSeenDate == null || x.LastSeenDate <= since)),
            "pending" => q.Where(x => x.Status == ClientStatus.Pending),
            "disabled" => q.Where(x => x.Status == ClientStatus.Disabled),
            "updates" => q.Where(x => x.UpdatesAvailable > 0),
            _ => q,
        };
        if (c.GroupId is { } group)
        {
            q = q.Where(x => db.ClientGroupMembers.Any(m => m.ClientId == x.Id && m.ClientGroupId == group));
        }

        if (!string.IsNullOrWhiteSpace(c.System))
        {
            var os = $"%{c.System}%";
            q = q.Where(x => EF.Functions.ILike(x.OperatingSystem ?? string.Empty, os));
        }

        if (!string.IsNullOrWhiteSpace(c.Update))
        {
            var update = c.Update.Trim();
            q = q.Where(x => db.ClientSoftware.Any(s => s.ClientId == x.Id && s.WingetId == update && s.AvailableVersion != null));
        }

        var matching = await q.CountAsync(ct);
        q = (c.Sort, c.Descending) switch
        {
            ("seen", false) => q.OrderBy(x => x.LastSeenDate).ThenBy(x => x.Name),
            ("seen", true) => q.OrderByDescending(x => x.LastSeenDate).ThenBy(x => x.Name),
            ("os", false) => q.OrderBy(x => x.OperatingSystem).ThenBy(x => x.Name),
            ("os", true) => q.OrderByDescending(x => x.OperatingSystem).ThenBy(x => x.Name),
            ("updates", false) => q.OrderBy(x => x.UpdatesAvailable).ThenBy(x => x.Name),
            ("updates", true) => q.OrderByDescending(x => x.UpdatesAvailable).ThenBy(x => x.Name),
            ("created", false) => q.OrderBy(x => x.CreateDate),
            ("created", true) => q.OrderByDescending(x => x.CreateDate),
            (_, true) => q.OrderByDescending(x => x.Name),
            _ => q.OrderBy(x => x.Name),
        };
        var rows = await q.Skip((c.Page - 1) * c.PageSize).Take(c.PageSize)
            .Select(x => new { x.Id, x.Name, x.Status, x.OperatingSystem, x.IpAddresses, x.LastUser, x.LastSeenDate, x.UpdatesAvailable, x.AgentVersion })
            .ToListAsync(ct);
        return new ClientList(
            rows.Select(x => new ClientRow(x.Id, x.Name, x.Status, IsOnline(x.LastSeenDate), x.OperatingSystem, x.IpAddresses, x.LastUser, x.LastSeenDate, x.UpdatesAvailable, x.AgentVersion)).ToList(),
            total, matching);
    }

    /// <summary>The numbers of the overview; null without the permission to see clients.</summary>
    public static async Task<ClientOverview?> OverviewAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ClientsView))
        {
            return null;
        }

        var since = DateTime.UtcNow - OnlineWindow;
        var rows = await AccessRules.Clients(db, actor).AsNoTracking()
            .Select(c => new { c.Status, Online = c.LastSeenDate > since, c.OperatingSystem, c.UpdatesAvailable })
            .ToListAsync(ct);
        var active = rows.Where(r => r.Status == ClientStatus.Active).ToList();
        var systems = active.Where(r => !string.IsNullOrWhiteSpace(r.OperatingSystem)).GroupBy(r => r.OperatingSystem!)
            .Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ThenBy(x => x.Key).ToList();
        return new ClientOverview(
            rows.Count, active.Count(r => r.Online), active.Count(r => !r.Online), rows.Count(r => r.Status == ClientStatus.Pending),
            rows.Count(r => r.Status == ClientStatus.Disabled), active.Count(r => r.UpdatesAvailable > 0), systems);
    }

    /// <summary>The operating systems of the clients the actor reaches (the filter of the list).</summary>
    public static async Task<IReadOnlyList<string>> SystemsAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
        => actor.Can(Permission.ClientsView)
            ? await AccessRules.Clients(db, actor).Where(c => c.OperatingSystem != null).Select(c => c.OperatingSystem!).Distinct().OrderBy(s => s).Take(50).ToListAsync(ct)
            : [];

    public static async Task<ClientDetail?> GetAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ClientsView))
        {
            return null;
        }

        var client = await AccessRules.Clients(db, actor).AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is null)
        {
            return null;
        }

        var groups = await db.ClientGroupMembers.AsNoTracking().Where(m => m.ClientId == id)
            .OrderBy(m => m.ClientGroup!.Name)
            .Select(m => new ClientGroupRef(m.ClientGroup!.Id, m.ClientGroup.Name, m.ClientGroup.Colour, m.ClientGroup.Kind))
            .ToListAsync(ct);
        return new ClientDetail(client, IsOnline(client.LastSeenDate), groups);
    }

    /// <summary>Saves what a person may change; the static groups are set to <see cref="ClientForm.GroupIds"/> (only groups the actor may choose), the dynamic ones follow the tags.</summary>
    public static async Task<Outcome<Client>> SaveAsync(AppDbContext db, Actor actor, long id, ClientForm form, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ClientsManage))
        {
            return Outcome<Client>.Denied();
        }

        var client = await AccessRules.Clients(db, actor).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is null)
        {
            return Outcome<Client>.Denied();
        }

        client.Description = Clean(form.Description, 1000);
        client.Tags = NormalizeTags(form.Tags);

        // the static groups the actor may choose: what was ticked is kept, what was not is removed; groups the actor cannot see stay as they are
        var choosable = await AccessRules.Groups(db, actor).Where(g => g.Kind == GroupKind.Static).Select(g => g.Id).ToListAsync(ct);
        var wanted = form.GroupIds.Where(choosable.Contains).ToHashSet();
        var current = await db.ClientGroupMembers.Where(m => m.ClientId == id && choosable.Contains(m.ClientGroupId)).ToListAsync(ct);
        db.ClientGroupMembers.RemoveRange(current.Where(m => !wanted.Contains(m.ClientGroupId)));
        foreach (var groupId in wanted.Where(g => current.All(m => m.ClientGroupId != g)))
        {
            db.ClientGroupMembers.Add(new ClientGroupMember { ClientGroupId = groupId, ClientId = id });
        }

        ActivityLog.Add(db, actor, "client", "update", client.Name, clientId: client.Id);
        await db.SaveChangesAsync(ct);
        await GroupService.RefreshClientAsync(db, client.Id, ct);
        return Outcome<Client>.Success(client);
    }

    /// <summary>Removes the client: its agent is refused from now on (it can enroll again with a key).</summary>
    public static async Task<Outcome<string>> DeleteAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ClientsManage))
        {
            return Outcome<string>.Denied();
        }

        var client = await AccessRules.Clients(db, actor).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is null)
        {
            return Outcome<string>.Denied();
        }

        db.Clients.Remove(client);
        await db.ClientGroupMembers.Where(m => m.ClientId == id).ExecuteDeleteAsync(ct);
        await db.JobRuns.Where(r => r.ClientId == id && (r.Status == RunStatus.Queued || r.Status == RunStatus.Running))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatus.Cancelled).SetProperty(r => r.FinishedDate, DateTime.UtcNow), ct);
        ActivityLog.Add(db, actor, "client", "delete", client.Name, clientId: client.Id);
        await db.SaveChangesAsync(ct);
        return Outcome<string>.Success(client.Name);
    }

    /// <summary>Active (approve a pending client, enable a disabled one) or disabled.</summary>
    public static async Task<Outcome<Client>> SetStatusAsync(AppDbContext db, Actor actor, long id, ClientStatus status, CancellationToken ct = default)
    {
        var permission = status == ClientStatus.Active ? Permission.ClientsEnroll : Permission.ClientsManage;
        if (!actor.Can(permission) || status == ClientStatus.Pending)
        {
            return Outcome<Client>.Denied();
        }

        var client = await AccessRules.Clients(db, actor).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is null)
        {
            return Outcome<Client>.Denied();
        }

        var action = client.Status == ClientStatus.Pending && status == ClientStatus.Active ? "approve" : status == ClientStatus.Active ? "enable" : "disable";
        client.Status = status;
        ActivityLog.Add(db, actor, "client", action, client.Name, clientId: client.Id);
        await db.SaveChangesAsync(ct);
        return Outcome<Client>.Success(client);
    }

    /// <summary>Asks the agent for an inventory with its next call.</summary>
    public static async Task<bool> RequestInventoryAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ClientsView))
        {
            return false;
        }

        return await AccessRules.Clients(db, actor).Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.InventoryRequested, true), ct) > 0;
    }

    public static async Task<IReadOnlyList<SoftwareRow>?> SoftwareAsync(AppDbContext db, Actor actor, long id, string? search, bool onlyUpdates, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ClientsView) || !await AccessRules.ReachesAsync(db, actor, id, ct))
        {
            return null;
        }

        var q = db.ClientSoftware.AsNoTracking().Where(s => s.ClientId == id);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = $"%{search.Trim()}%";
            q = q.Where(x => EF.Functions.ILike(x.Name, s) || EF.Functions.ILike(x.Publisher ?? string.Empty, s) || EF.Functions.ILike(x.WingetId ?? string.Empty, s));
        }

        if (onlyUpdates)
        {
            q = q.Where(x => x.AvailableVersion != null);
        }

        return await q.OrderBy(x => x.Name).Select(x => new SoftwareRow(x.Name, x.Version, x.Publisher, x.WingetId, x.AvailableVersion)).ToListAsync(ct);
    }

    /// <summary>The clients the actor reaches, for a choice (run a script on …): id, name, online.</summary>
    public static async Task<IReadOnlyList<(long Id, string Name, bool Online)>> ChoicesAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        var rows = await AccessRules.Clients(db, actor).Where(c => c.Status == ClientStatus.Active).OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name, c.LastSeenDate }).Take(5000).ToListAsync(ct);
        return rows.Select(r => (r.Id, r.Name, IsOnline(r.LastSeenDate))).ToList();
    }

    public static string NormalizeTags(string? tags)
    {
        var list = (tags ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Length > 40 ? t[..40] : t).Distinct(StringComparer.OrdinalIgnoreCase).Take(20);
        var joined = string.Join(',', list);
        return joined.Length > 500 ? joined[..500] : joined;
    }

    internal static string? Clean(string? text, int max)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > max ? t[..max] : t;
    }
}
