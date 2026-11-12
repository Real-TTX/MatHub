using MatHub.Data;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services.Access;

/// <summary>
/// The permissions of MatHub. An administrator has all of them; a user has those of their <see cref="AccessRole"/> (and those MatIdentity added), for the clients the
/// role reaches. The keys are part of the platform definition (<c>o.Permission</c>), so MatIdentity can grant them.
/// </summary>
public static class Permission
{
    public const string ClientsView = "clients-view";
    public const string ClientsManage = "clients-manage";
    public const string ClientsEnroll = "clients-enroll";
    public const string GroupsManage = "groups-manage";
    public const string ScriptsView = "scripts-view";
    public const string ScriptsEdit = "scripts-edit";
    public const string ScriptsRun = "scripts-run";
    public const string PackagesView = "packages-view";
    public const string PackagesEdit = "packages-edit";
    public const string PackagesDeploy = "packages-deploy";
    public const string JobsView = "jobs-view";
    public const string JobsManage = "jobs-manage";
    public const string RolloutView = "rollout-view";
    public const string RolloutManage = "rollout-manage";

    /// <summary>Every permission, in the order of the role page (grouped by area).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ClientsView, ClientsManage, ClientsEnroll, GroupsManage,
        ScriptsView, ScriptsEdit, ScriptsRun,
        PackagesView, PackagesEdit, PackagesDeploy,
        JobsView, JobsManage,
        RolloutView, RolloutManage,
    ];

    /// <summary>The area of a permission (<c>clients</c> of <c>clients-view</c>); the platform allows small letters, digits, - and _ in a key.</summary>
    public static string AreaOf(string permission) => permission[..permission.IndexOf('-')];

    /// <summary>The known permissions of a comma separated text, without duplicates.</summary>
    public static IReadOnlySet<string> Parse(string? text)
        => (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => All.Contains(p)).ToHashSet(StringComparer.Ordinal);

    public static string Join(IEnumerable<string> permissions) => string.Join(',', All.Where(permissions.Contains));
}

/// <summary>
/// Who acts: a person with their permissions and the clients they reach, or MatHub itself (<see cref="System"/>: the planner, the agent API). Every function of the
/// business layer takes the actor and checks it: the pages hide what a person may not do, but the business layer is where it is decided.
/// </summary>
/// <param name="GroupIds">The groups whose clients the person reaches, when <paramref name="AllClients"/> is false.</param>
public sealed record Actor(long? UserId, bool IsAdmin, IReadOnlySet<string> Permissions, bool AllClients, IReadOnlyList<long> GroupIds)
{
    /// <summary>MatHub itself: background work and the calls of the agents.</summary>
    public static Actor System { get; } = new(null, true, new HashSet<string>(), true, []);

    public bool Can(string permission) => IsAdmin || Permissions.Contains(permission);

    /// <summary>Reaches every client (an administrator, or a role for all clients).</summary>
    public bool ReachesAll => IsAdmin || AllClients;
}

/// <summary>The checks of the business layer: permissions and the clients an actor reaches.</summary>
public static class AccessRules
{
    /// <summary>The clients the actor reaches (all, or the members of the groups of their role).</summary>
    public static IQueryable<Client> Clients(AppDbContext db, Actor actor)
    {
        if (actor.ReachesAll)
        {
            return db.Clients;
        }

        var groups = actor.GroupIds.ToList();
        return db.Clients.Where(c => db.ClientGroupMembers.Any(m => m.ClientId == c.Id && groups.Contains(m.ClientGroupId)));
    }

    /// <summary>The groups the actor may choose as targets: all, or those of their role.</summary>
    public static IQueryable<ClientGroup> Groups(AppDbContext db, Actor actor)
    {
        if (actor.ReachesAll)
        {
            return db.ClientGroups;
        }

        var groups = actor.GroupIds.ToList();
        return db.ClientGroups.Where(g => groups.Contains(g.Id));
    }

    /// <summary>Whether the actor reaches the client.</summary>
    public static Task<bool> ReachesAsync(AppDbContext db, Actor actor, long clientId, CancellationToken ct = default)
        => Clients(db, actor).AnyAsync(c => c.Id == clientId, ct);

    /// <summary>The ids of those of <paramref name="clientIds"/> the actor reaches.</summary>
    public static async Task<List<long>> ReachableAsync(AppDbContext db, Actor actor, IEnumerable<long> clientIds, CancellationToken ct = default)
    {
        var ids = clientIds.Distinct().ToList();
        return await Clients(db, actor).Where(c => ids.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct);
    }

    /// <summary>The permissions and the reach of a user, read from their role (the snapshot of the session keeps them for the request).</summary>
    public static async Task<(IReadOnlySet<string> Permissions, bool AllClients, IReadOnlyList<long> GroupIds)> LoadAsync(AppDbContext db, User user, CancellationToken ct = default)
    {
        var permissions = new HashSet<string>(Permission.Parse(user.IdentityPermissions), StringComparer.Ordinal);
        if (user.AccessRoleId is not { } roleId)
        {
            return (permissions, false, []);
        }

        var role = await db.AccessRoles.AsNoTracking().Where(r => r.Id == roleId)
            .Select(r => new { r.Permissions, r.AllClients, Groups = r.Groups.Select(g => g.ClientGroupId).ToList() })
            .FirstOrDefaultAsync(ct);
        if (role is null)
        {
            return (permissions, false, []);
        }

        permissions.UnionWith(Permission.Parse(role.Permissions));
        return (permissions, role.AllClients, role.Groups);
    }
}
