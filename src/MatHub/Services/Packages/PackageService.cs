using MatHub.Data;
using MatHub.Services.Access;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services.Packages;

/// <param name="Version">The approved version; <paramref name="Testing"/> the one in test; <paramref name="Waiting"/> how many versions wait for an approval.</param>
/// <param name="Current">Clients with the approved version (or newer); <paramref name="Installed"/> all that have it in any version.</param>
public sealed record PackageRow(
    long Id, string Name, string? Publisher, PackageKind Kind, string? Version, string? Testing, int Waiting, IReadOnlyList<InstallerArchitecture> Platforms,
    string? WingetId, int Assignments, int Installed, int Current);

/// <summary>What a package is (its versions and installers are kept by <see cref="PackageVersions"/>).</summary>
/// <param name="WingetId">Its id in the catalog of winget: new versions are found there.</param>
/// <param name="AutoUpdate">New versions of the catalog are prepared by themselves (never approved).</param>
public sealed record PackageForm(
    string Name, string? Publisher, string? Description, string? WingetId, DetectionKind Detection, string? DetectionValue, bool AutoUpdate, long? PilotGroupId = null);

public sealed record AssignmentRow(long Id, long GroupId, string GroupName, string? GroupColour, AssignmentAction Action, bool IsActive, int Members, int Installed);

/// <summary>
/// The packages (static business layer): winget packages, uploaded MSI and EXE installers and scripted ones, and their assignments to groups. An assignment is a
/// wish ("the laptops have Firefox"); <see cref="Deployments.Planner"/> makes the jobs that fulfil it and keeps winget packages up to date.
/// </summary>
public static class PackageService
{
    public static async Task<IReadOnlyList<PackageRow>?> ListAsync(AppDbContext db, Actor actor, string? search, string? kind, string sort, bool desc, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.PackagesView))
        {
            return null;
        }

        var q = db.Packages.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = $"%{search.Trim()}%";
            q = q.Where(p => EF.Functions.ILike(p.Name, s) || EF.Functions.ILike(p.Publisher ?? string.Empty, s) || EF.Functions.ILike(p.WingetId ?? string.Empty, s));
        }

        if (Enum.TryParse<PackageKind>(kind, true, out var k))
        {
            q = q.Where(p => p.Kind == k);
        }
        else if (kind == "waiting")
        {
            q = q.Where(p => p.Versions.Any(v => v.Status == VersionStatus.New || v.Status == VersionStatus.Testing));
        }

        q = (sort, desc) switch
        {
            ("kind", false) => q.OrderBy(p => p.Kind).ThenBy(p => p.Name),
            ("kind", true) => q.OrderByDescending(p => p.Kind).ThenBy(p => p.Name),
            ("created", false) => q.OrderBy(p => p.CreateDate),
            ("created", true) => q.OrderByDescending(p => p.CreateDate),
            (_, true) => q.OrderByDescending(p => p.Name),
            _ => q.OrderBy(p => p.Name),
        };
        var reachable = AccessRules.Clients(db, actor).Select(c => c.Id);
        var rows = await q.Select(p => new
        {
            p.Id, p.Name, p.Publisher, p.Kind, p.Version, p.WingetId, p.Detection, p.DetectionValue,
            Assignments = p.Assignments.Count,
            Testing = p.Versions.Where(v => v.Status == VersionStatus.Testing).Select(v => v.Version).FirstOrDefault(),
            Waiting = p.Versions.Count(v => v.Status == VersionStatus.New),
            Platforms = p.Versions.Where(v => v.Status == VersionStatus.Approved).SelectMany(v => v.Installers).Select(i => i.Architecture).ToList(),
        }).ToListAsync(ct);

        var result = new List<PackageRow>();
        foreach (var p in rows)
        {
            var versions = await InstalledQuery(db, p.Detection, p.DetectionValue, p.WingetId).Where(s => reachable.Contains(s.ClientId))
                .Select(s => new { s.ClientId, s.Version }).ToListAsync(ct);
            var installed = versions.Select(v => v.ClientId).Distinct().Count();
            var current = p.Version is null ? 0 : !PackageVersions.Comparable(p.Version) ? installed : versions.Where(v => v.Version is not null && WingetCatalog.CompareVersions(v.Version, p.Version) >= 0).Select(v => v.ClientId).Distinct().Count();
            result.Add(new PackageRow(p.Id, p.Name, p.Publisher, p.Kind, p.Version, p.Testing, p.Waiting, p.Platforms.Distinct().Order().ToList(), p.WingetId, p.Assignments, installed, current));
        }

        return result;
    }

    /// <summary>The software of the clients that counts as this package (by the detection of the package).</summary>
    public static IQueryable<ClientSoftware> InstalledQuery(AppDbContext db, DetectionKind detection, string? value, string? wingetId)
    {
        switch (detection)
        {
            case DetectionKind.WingetId when !string.IsNullOrWhiteSpace(value ?? wingetId):
                var id = (value ?? wingetId)!.Trim().ToLower();
                return db.ClientSoftware.Where(s => s.WingetId != null && s.WingetId.ToLower() == id);
            case DetectionKind.DisplayName when !string.IsNullOrWhiteSpace(value):
                var name = $"%{value.Trim()}%";
                return db.ClientSoftware.Where(s => EF.Functions.ILike(s.Name, name));
            default:
                return db.ClientSoftware.Where(s => false);
        }
    }

    public static async Task<Package?> GetAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
        => actor.Can(Permission.PackagesView) ? await db.Packages.AsNoTracking().Include(p => p.PilotGroup).FirstOrDefaultAsync(p => p.Id == id, ct) : null;

    /// <summary>The packages by their winget id (case does not matter): which program of a client is a package already.</summary>
    public static async Task<IReadOnlyDictionary<string, long>> ByWingetIdAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.PackagesView))
        {
            return new Dictionary<string, long>();
        }

        var rows = await db.Packages.AsNoTracking().Where(p => p.WingetId != null).Select(p => new { p.Id, p.WingetId }).ToListAsync(ct);
        return rows.GroupBy(r => r.WingetId!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The name of a program without the version and the architecture Windows adds ("7-Zip 23.01 (x64)" is "7-Zip").</summary>
    public static string CleanName(string name)
    {
        var n = System.Text.RegularExpressions.Regex.Replace(name.Trim(), @"\s*\((x64|x86|64-bit|32-bit|arm64)[^)]*\)", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        n = System.Text.RegularExpressions.Regex.Replace(n, @"\s+v?\d+(\.\d+)+\S*$", string.Empty);
        return n.Length == 0 ? name.Trim() : n;
    }

    /// <summary>A program a client has becomes a package through the store: its page in the catalog (name, publisher, versions, installers).</summary>
    public static string NewFromWingetUrl(string wingetId, string name, string? publisher)
        => $"/store/item?id={Uri.EscapeDataString(wingetId)}";

    public static async Task<IReadOnlyList<(long Id, string Name, PackageKind Kind)>> ChoicesAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.PackagesView))
        {
            return [];
        }

        var rows = await db.Packages.AsNoTracking().OrderBy(p => p.Name).Select(p => new { p.Id, p.Name, p.Kind }).ToListAsync(ct);
        return rows.Select(r => (r.Id, r.Name, r.Kind)).ToList();
    }

    public static async Task<Outcome<Package>> SaveAsync(AppDbContext db, Actor actor, long? id, PackageForm form, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.PackagesEdit))
        {
            return Outcome<Package>.Denied();
        }

        var name = form.Name.Trim();
        if (name.Length == 0)
        {
            return Outcome<Package>.Fail(Localization.Text.T("Common.NameRequired"));
        }

        if (form.Detection == DetectionKind.WingetId && string.IsNullOrWhiteSpace(form.DetectionValue ?? form.WingetId))
        {
            return Outcome<Package>.Fail(Localization.Text.T("Packages.WingetId.Required"));
        }

        if (form.PilotGroupId is { } pilot && !await AccessRules.Groups(db, actor).AnyAsync(g => g.Id == pilot, ct))
        {
            return Outcome<Package>.Denied();
        }

        Package package;
        if (id is { } existing)
        {
            var found = await db.Packages.FirstOrDefaultAsync(p => p.Id == existing, ct);
            if (found is null)
            {
                return Outcome<Package>.Denied();
            }

            package = found;
        }
        else
        {
            package = new Package();
            db.Packages.Add(package);
        }

        package.Name = Cut(name, 120)!;
        package.Publisher = Cut(form.Publisher, 120);
        package.Description = Cut(form.Description, 1000);
        package.WingetId = Cut(form.WingetId, 200);
        package.Detection = form.Detection;
        package.DetectionValue = form.Detection == DetectionKind.WingetId && string.IsNullOrWhiteSpace(form.DetectionValue) ? null : Cut(form.DetectionValue, 300);
        package.AutoUpdate = package.WingetId is not null && form.AutoUpdate;
        package.PilotGroupId = form.PilotGroupId;
        ActivityLog.Add(db, actor, "package", id is null ? "create" : "update", package.Name);
        await db.SaveChangesAsync(ct);
        return Outcome<Package>.Success(package);
    }

    public static async Task<Outcome<string>> DeleteAsync(AppDbContext db, Actor actor, PackageFiles store, long id, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.PackagesEdit))
        {
            return Outcome<string>.Denied();
        }

        var package = await db.Packages.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (package is null)
        {
            return Outcome<string>.Denied();
        }

        db.Packages.Remove(package);
        await db.PackageAssignments.Where(a => a.PackageId == id).ExecuteDeleteAsync(ct);
        ActivityLog.Add(db, actor, "package", "delete", package.Name);
        await db.SaveChangesAsync(ct);
        store.DeleteAll(id);
        return Outcome<string>.Success(package.Name);
    }

    // ---- assignments -----------------------------------------------------------------------------------------------------------

    public static async Task<IReadOnlyList<AssignmentRow>> AssignmentsAsync(AppDbContext db, Actor actor, long packageId, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.PackagesView))
        {
            return [];
        }

        var package = await db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == packageId, ct);
        if (package is null)
        {
            return [];
        }

        var groups = AccessRules.Groups(db, actor).Select(g => g.Id);
        var installed = InstalledQuery(db, package.Detection, package.DetectionValue, package.WingetId).Select(s => s.ClientId);
        return await db.PackageAssignments.AsNoTracking().Where(a => a.PackageId == packageId && groups.Contains(a.ClientGroupId))
            .OrderBy(a => a.ClientGroup!.Name)
            .Select(a => new AssignmentRow(
                a.Id, a.ClientGroupId, a.ClientGroup!.Name, a.ClientGroup.Colour, a.Action, a.IsActive,
                a.ClientGroup.Members.Count(m => m.Client!.UpdateState != SoftEntity.Deleted),
                a.ClientGroup.Members.Count(m => m.Client!.UpdateState != SoftEntity.Deleted && installed.Contains(m.ClientId))))
            .ToListAsync(ct);
    }

    public static async Task<Outcome<PackageAssignment>> AssignAsync(AppDbContext db, Actor actor, long packageId, long groupId, AssignmentAction action, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.PackagesDeploy) || !await AccessRules.Groups(db, actor).AnyAsync(g => g.Id == groupId, ct))
        {
            return Outcome<PackageAssignment>.Denied();
        }

        var package = await db.Packages.FirstOrDefaultAsync(p => p.Id == packageId, ct);
        if (package is null)
        {
            return Outcome<PackageAssignment>.Denied();
        }

        // what is assigned is the approved version: it has to be there, and able to do what the assignment says
        var (approved, _) = await PackageVersions.CurrentAsync(db, packageId, ct);
        if (approved is null || PackageVersions.Template(approved, action == AssignmentAction.Uninstall) is null)
        {
            return Outcome<PackageAssignment>.Fail(Localization.Text.T(approved is null ? "Versions.Error.NoneApproved" : action == AssignmentAction.Uninstall ? "Jobs.Error.NoUninstall" : "Jobs.Error.NoInstaller"));
        }

        var assignment = await db.PackageAssignments.FirstOrDefaultAsync(a => a.PackageId == packageId && a.ClientGroupId == groupId, ct);
        if (assignment is null)
        {
            assignment = new PackageAssignment { PackageId = packageId, ClientGroupId = groupId };
            db.PackageAssignments.Add(assignment);
        }

        assignment.Action = action;
        assignment.IsActive = true;
        ActivityLog.Add(db, actor, "package", "assign", package.Name, action.ToString());
        await db.SaveChangesAsync(ct);
        return Outcome<PackageAssignment>.Success(assignment);
    }

    public static async Task<bool> UnassignAsync(AppDbContext db, Actor actor, long assignmentId, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.PackagesDeploy))
        {
            return false;
        }

        var groups = AccessRules.Groups(db, actor).Select(g => g.Id);
        var assignment = await db.PackageAssignments.Include(a => a.Package).FirstOrDefaultAsync(a => a.Id == assignmentId && groups.Contains(a.ClientGroupId), ct);
        if (assignment is null)
        {
            return false;
        }

        db.PackageAssignments.Remove(assignment);
        ActivityLog.Add(db, actor, "package", "unassign", assignment.Package?.Name);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static string? Cut(string? text, int max)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > max ? t[..max] : t;
    }
}
