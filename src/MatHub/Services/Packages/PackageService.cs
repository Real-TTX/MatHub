using MatHub.Data;
using MatHub.Services.Access;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services.Packages;

public sealed record PackageRow(long Id, string Name, string? Publisher, PackageKind Kind, string? Version, string? WingetId, int Assignments, int Installed);

public sealed record PackageForm(
    string Name, string? Publisher, string? Description, PackageKind Kind, string? Version, string? WingetId, string? InstallArguments, string? UninstallCommand,
    string? InstallScript, DetectionKind Detection, string? DetectionValue, bool AutoUpdate);

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
        }).ToListAsync(ct);

        var result = new List<PackageRow>();
        foreach (var p in rows)
        {
            var installed = await InstalledQuery(db, p.Detection, p.DetectionValue, p.WingetId).Where(s => reachable.Contains(s.ClientId)).Select(s => s.ClientId).Distinct().CountAsync(ct);
            result.Add(new PackageRow(p.Id, p.Name, p.Publisher, p.Kind, p.Version, p.WingetId, p.Assignments, installed));
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
        => actor.Can(Permission.PackagesView) ? await db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) : null;

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

    /// <summary>The address of a new package filled in from a program a client has (winget id, name, publisher).</summary>
    public static string NewFromWingetUrl(string wingetId, string name, string? publisher)
        => $"/packages/edit?winget={Uri.EscapeDataString(wingetId)}&name={Uri.EscapeDataString(name)}" + (string.IsNullOrEmpty(publisher) ? string.Empty : $"&publisher={Uri.EscapeDataString(publisher)}");

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

        if (form.Kind == PackageKind.Winget && string.IsNullOrWhiteSpace(form.WingetId))
        {
            return Outcome<Package>.Fail(Localization.Text.T("Packages.WingetId.Required"));
        }

        if (form.Kind == PackageKind.Script && string.IsNullOrWhiteSpace(form.InstallScript))
        {
            return Outcome<Package>.Fail(Localization.Text.T("Packages.InstallScript.Required"));
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
        package.Kind = form.Kind;
        package.Version = form.Kind == PackageKind.Winget ? null : Cut(form.Version, 60);
        package.WingetId = form.Kind == PackageKind.Winget ? Cut(form.WingetId, 200) : null;
        package.InstallArguments = form.Kind is PackageKind.Msi or PackageKind.Exe ? Cut(form.InstallArguments, 1000) : null;
        package.UninstallCommand = form.Kind is PackageKind.Exe or PackageKind.Script ? Cut(form.UninstallCommand, 4000) : null;
        package.InstallScript = form.Kind == PackageKind.Script ? form.InstallScript?.Trim() : null;
        package.Detection = form.Kind == PackageKind.Winget && form.Detection == DetectionKind.None ? DetectionKind.WingetId : form.Detection;
        package.DetectionValue = package.Detection == DetectionKind.WingetId && form.Kind == PackageKind.Winget && string.IsNullOrWhiteSpace(form.DetectionValue)
            ? null
            : Cut(form.DetectionValue, 300);
        package.AutoUpdate = form.Kind == PackageKind.Winget && form.AutoUpdate;
        ActivityLog.Add(db, actor, "package", id is null ? "create" : "update", package.Name);
        await db.SaveChangesAsync(ct);
        return Outcome<Package>.Success(package);
    }

    public static async Task<Outcome<Package>> UploadAsync(AppDbContext db, Actor actor, PackageFiles store, long id, string fileName, Stream content, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.PackagesEdit))
        {
            return Outcome<Package>.Denied();
        }

        var package = await db.Packages.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (package is null)
        {
            return Outcome<Package>.Denied();
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if ((package.Kind == PackageKind.Msi && extension != ".msi") || (package.Kind == PackageKind.Exe && extension != ".exe") || package.Kind is PackageKind.Winget or PackageKind.Script)
        {
            return Outcome<Package>.Fail(Localization.Text.T("Packages.File.WrongType"));
        }

        var (name, size, sha) = await store.SaveAsync(id, fileName, content, ct);
        package.FileName = name;
        package.FileSize = size;
        package.FileSha256 = sha;
        ActivityLog.Add(db, actor, "package", "upload", package.Name, name);
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

        if (Jobs.JobService.PackageTemplate(package, action == AssignmentAction.Uninstall) is null)
        {
            return Outcome<PackageAssignment>.Fail(Localization.Text.T(action == AssignmentAction.Uninstall ? "Jobs.Error.NoUninstall" : "Jobs.Error.NoInstaller"));
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
