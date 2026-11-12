using MatHub.Data;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services.Access;

public sealed record RoleRow(long Id, string? Key, string Name, string? Description, int Permissions, bool AllClients, int Groups, int Users);

public sealed record RoleForm(string Name, string? Description, IReadOnlyList<string> Permissions, bool AllClients, IReadOnlyList<long> GroupIds);

/// <summary>
/// The roles (permission sets) of MatHub, administered by the administrators: which permissions, for all clients or the members of some groups. Three built-in
/// roles carry a key (<see cref="Technician"/>, <see cref="Operator"/>, <see cref="Viewer"/>): the platform definition names them, so MatIdentity can give
/// them; they can be changed but not deleted.
/// </summary>
public static class RoleService
{
    public const string Technician = "technician";
    public const string Operator = "operator";
    public const string Viewer = "viewer";

    /// <summary>Makes the built-in roles when they are missing (at the start).</summary>
    public static async Task SeedAsync(AppDbContext db, CancellationToken ct = default)
    {
        var keys = await db.AccessRoles.IgnoreQueryFilters().Where(r => r.Key != null).Select(r => r.Key!).ToListAsync(ct);
        var views = Permission.All.Where(p => p.EndsWith("-view", StringComparison.Ordinal)).ToList();
        var seeds = new (string Key, string Name, IEnumerable<string> Permissions)[]
        {
            (Technician, "Technician", Permission.All),
            (Operator, "Operator", views.Concat([Permission.ScriptsRun, Permission.PackagesDeploy, Permission.JobsManage])),
            (Viewer, "Viewer", views),
        };
        foreach (var (key, name, permissions) in seeds.Where(s => !keys.Contains(s.Key)))
        {
            db.AccessRoles.Add(new AccessRole { Key = key, Name = name, Permissions = Permission.Join(permissions), AllClients = true });
        }

        await db.SaveChangesAsync(ct);
    }

    public static async Task<IReadOnlyList<RoleRow>?> ListAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return null;
        }

        var rows = await db.AccessRoles.AsNoTracking().OrderBy(r => r.Key == null).ThenBy(r => r.Name)
            .Select(r => new { r.Id, r.Key, r.Name, r.Description, r.Permissions, r.AllClients, Groups = r.Groups.Count, Users = db.Users.Count(u => u.AccessRoleId == r.Id) })
            .ToListAsync(ct);
        return rows.Select(r => new RoleRow(r.Id, r.Key, r.Name, r.Description, Permission.Parse(r.Permissions).Count, r.AllClients, r.Groups, r.Users)).ToList();
    }

    public static async Task<IReadOnlyList<(long Id, string Name)>> ChoicesAsync(AppDbContext db, CancellationToken ct = default)
    {
        var rows = await db.AccessRoles.AsNoTracking().OrderBy(r => r.Name).Select(r => new { r.Id, r.Name }).ToListAsync(ct);
        return rows.Select(r => (r.Id, r.Name)).ToList();
    }

    public static async Task<(AccessRole Role, IReadOnlyList<long> GroupIds)?> GetAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return null;
        }

        var role = await db.AccessRoles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (role is null)
        {
            return null;
        }

        var groups = await db.AccessRoleGroups.Where(g => g.AccessRoleId == id).Select(g => g.ClientGroupId).ToListAsync(ct);
        return (role, groups);
    }

    public static async Task<Outcome<AccessRole>> SaveAsync(AppDbContext db, Actor actor, long? id, RoleForm form, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return Outcome<AccessRole>.Denied();
        }

        var name = form.Name.Trim();
        if (name.Length == 0)
        {
            return Outcome<AccessRole>.Fail(Localization.Text.T("Common.NameRequired"));
        }

        if (!form.AllClients && form.GroupIds.Count == 0)
        {
            return Outcome<AccessRole>.Fail(Localization.Text.T("Roles.Groups.Required"));
        }

        AccessRole role;
        if (id is { } existing)
        {
            var found = await db.AccessRoles.FirstOrDefaultAsync(r => r.Id == existing, ct);
            if (found is null)
            {
                return Outcome<AccessRole>.Denied();
            }

            role = found;
        }
        else
        {
            role = new AccessRole();
            db.AccessRoles.Add(role);
        }

        role.Name = name.Length > 120 ? name[..120] : name;
        role.Description = string.IsNullOrWhiteSpace(form.Description) ? null : form.Description.Trim();
        role.Permissions = Permission.Join(form.Permissions);
        role.AllClients = form.AllClients;
        await db.SaveChangesAsync(ct);

        var known = await db.ClientGroups.Where(g => form.GroupIds.Contains(g.Id)).Select(g => g.Id).ToListAsync(ct);
        var wanted = form.AllClients ? [] : known.ToHashSet();
        var current = await db.AccessRoleGroups.Where(g => g.AccessRoleId == role.Id).ToListAsync(ct);
        db.AccessRoleGroups.RemoveRange(current.Where(g => !wanted.Contains(g.ClientGroupId)));
        db.AccessRoleGroups.AddRange(wanted.Where(g => current.All(c => c.ClientGroupId != g)).Select(g => new AccessRoleGroup { AccessRoleId = role.Id, ClientGroupId = g }));
        ActivityLog.Add(db, actor, "role", id is null ? "create" : "update", role.Name);
        await db.SaveChangesAsync(ct);
        SessionService.Invalidate();
        return Outcome<AccessRole>.Success(role);
    }

    public static async Task<Outcome<string>> DeleteAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return Outcome<string>.Denied();
        }

        var role = await db.AccessRoles.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (role is null || role.Key is not null)
        {
            return Outcome<string>.Denied();
        }

        db.AccessRoles.Remove(role);
        await db.Users.Where(u => u.AccessRoleId == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.AccessRoleId, (long?)null), ct);
        ActivityLog.Add(db, actor, "role", "delete", role.Name);
        await db.SaveChangesAsync(ct);
        SessionService.Invalidate();
        return Outcome<string>.Success(role.Name);
    }
}
