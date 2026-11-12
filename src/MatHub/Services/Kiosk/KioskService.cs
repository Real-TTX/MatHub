using MatHub.Data;
using MatHub.Protocol;
using MatHub.Services.Access;
using MatHub.Services.Jobs;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services.Kiosk;

public sealed record KioskRow(long Id, string Name, string? Description, string? Category, string Icon, KioskKind Kind, string Target, int Groups, bool IsActive, int SortOrder, int Runs);

public sealed record KioskForm(
    string Name, string? Description, string? Category, string Icon, KioskKind Kind, long? ScriptId, long? PackageId, long? SequenceId,
    IReadOnlyList<long> GroupIds, string? Confirm, bool IsActive, int SortOrder);

/// <summary>
/// The kiosk (static business layer): offers the people at a computer start themselves, from the window of the agent. An offer is a script, the install of a
/// package or a sequence, for every client or the clients of groups. The agent asks for the offers when its kiosk opens (<see cref="OfferAsync"/>: always the
/// current list, with the last run on this computer), and a start makes a job for this one client (<see cref="StartAsync"/>), which the agent takes at once.
/// Publishing an offer needs the permission to run it: who may not install a package cannot let others install it either.
/// </summary>
public static class KioskService
{
    /// <summary>The icons of the tiles (the agent draws each with a glyph of Segoe Fluent Icons).</summary>
    public static IReadOnlyList<string> Icons { get; } =
        ["box", "download", "wrench", "printer", "network", "wifi", "shield", "lock", "key", "user", "users", "terminal", "refresh", "monitor", "folder", "mail", "globe", "settings", "rocket", "broom", "help-circle", "star"];

    public static async Task<IReadOnlyList<KioskRow>?> ListAsync(AppDbContext db, Actor actor, string? search, string? category, string sort, bool desc, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.KioskManage))
        {
            return null;
        }

        var q = db.KioskItems.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = $"%{search.Trim()}%";
            q = q.Where(x => EF.Functions.ILike(x.Name, s) || EF.Functions.ILike(x.Description ?? string.Empty, s));
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            q = q.Where(x => x.Category == category);
        }

        q = (sort, desc) switch
        {
            ("name", false) => q.OrderBy(x => x.Name),
            ("name", true) => q.OrderByDescending(x => x.Name),
            ("category", false) => q.OrderBy(x => x.Category).ThenBy(x => x.SortOrder).ThenBy(x => x.Name),
            ("category", true) => q.OrderByDescending(x => x.Category).ThenBy(x => x.SortOrder).ThenBy(x => x.Name),
            _ => q.OrderBy(x => x.SortOrder).ThenBy(x => x.Name),
        };
        var rows = await q.Select(x => new
        {
            x.Id, x.Name, x.Description, x.Category, x.Icon, x.Kind, x.GroupIds, x.IsActive, x.SortOrder,
            Script = db.Scripts.Where(s => s.Id == x.ScriptId).Select(s => s.Name).FirstOrDefault(),
            Package = db.Packages.Where(p => p.Id == x.PackageId).Select(p => p.Name).FirstOrDefault(),
            Sequence = db.Sequences.Where(s => s.Id == x.SequenceId).Select(s => s.Name).FirstOrDefault(),
            Runs = db.Jobs.Count(j => j.KioskItemId == x.Id),
        }).ToListAsync(ct);
        var missing = Localization.Text.T("Sequences.Step.Missing");
        return rows.Select(r => new KioskRow(r.Id, r.Name, r.Description, r.Category, r.Icon, r.Kind,
            (r.Kind switch { KioskKind.Package => r.Package, KioskKind.Sequence => r.Sequence, _ => r.Script }) ?? missing,
            r.GroupIds.Count, r.IsActive, r.SortOrder, r.Runs)).ToList();
    }

    public static async Task<IReadOnlyList<string>> CategoriesAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
        => actor.Can(Permission.KioskManage)
            ? await db.KioskItems.Where(s => s.Category != null).Select(s => s.Category!).Distinct().OrderBy(c => c).ToListAsync(ct)
            : [];

    public static async Task<KioskItem?> GetAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
        => actor.Can(Permission.KioskManage) ? await db.KioskItems.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) : null;

    public static async Task<Outcome<KioskItem>> SaveAsync(AppDbContext db, Actor actor, long? id, KioskForm form, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.KioskManage))
        {
            return Outcome<KioskItem>.Denied();
        }

        var name = form.Name.Trim();
        if (name.Length == 0)
        {
            return Outcome<KioskItem>.Fail(Localization.Text.T("Common.NameRequired"));
        }

        // what is offered has to be there, and the person has to be allowed to run it
        switch (form.Kind)
        {
            case KioskKind.Script when form.ScriptId is not { } sid || !await db.Scripts.AnyAsync(s => s.Id == sid, ct):
                return Outcome<KioskItem>.Fail(Localization.Text.T("Kiosk.Error.Script"));
            case KioskKind.Package when form.PackageId is not { } pid || !await db.Packages.AnyAsync(p => p.Id == pid, ct):
                return Outcome<KioskItem>.Fail(Localization.Text.T("Kiosk.Error.Package"));
            case KioskKind.Sequence when form.SequenceId is not { } qid || !await db.Sequences.AnyAsync(s => s.Id == qid, ct):
                return Outcome<KioskItem>.Fail(Localization.Text.T("Kiosk.Error.Sequence"));
        }

        if (!actor.Can(form.Kind == KioskKind.Package ? Permission.PackagesDeploy : Permission.ScriptsRun))
        {
            return Outcome<KioskItem>.Fail(Localization.Text.T("Kiosk.Error.MayNotRun"));
        }

        var groups = form.GroupIds.Distinct().ToList();
        var known = await AccessRules.Groups(db, actor).Where(g => groups.Contains(g.Id)).Select(g => g.Id).ToListAsync(ct);
        if (!actor.ReachesAll && known.Count == 0)
        {
            // a person who reaches only some groups offers only to them
            return Outcome<KioskItem>.Fail(Localization.Text.T("Kiosk.Error.Groups"));
        }

        KioskItem item;
        if (id is { } existing)
        {
            var found = await db.KioskItems.FirstOrDefaultAsync(s => s.Id == existing, ct);
            if (found is null)
            {
                return Outcome<KioskItem>.Denied();
            }

            item = found;
        }
        else
        {
            item = new KioskItem();
            db.KioskItems.Add(item);
        }

        item.Name = name.Length > 120 ? name[..120] : name;
        item.Description = Cut(form.Description, 1000);
        item.Category = Cut(form.Category, 60);
        item.Icon = Icons.Contains(form.Icon) ? form.Icon : "box";
        item.Kind = form.Kind;
        item.ScriptId = form.Kind == KioskKind.Script ? form.ScriptId : null;
        item.PackageId = form.Kind == KioskKind.Package ? form.PackageId : null;
        item.SequenceId = form.Kind == KioskKind.Sequence ? form.SequenceId : null;
        item.GroupIds = known;
        item.Confirm = Cut(form.Confirm, 500);
        item.IsActive = form.IsActive;
        item.SortOrder = Math.Clamp(form.SortOrder, 0, 9999);
        ActivityLog.Add(db, actor, "kiosk", id is null ? "create" : "update", item.Name);
        await db.SaveChangesAsync(ct);
        return Outcome<KioskItem>.Success(item);
    }

    public static async Task<Outcome<string>> DeleteAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.KioskManage))
        {
            return Outcome<string>.Denied();
        }

        var item = await db.KioskItems.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (item is null)
        {
            return Outcome<string>.Denied();
        }

        db.KioskItems.Remove(item);
        ActivityLog.Add(db, actor, "kiosk", "delete", item.Name);
        await db.SaveChangesAsync(ct);
        return Outcome<string>.Success(item.Name);
    }

    // ---- the agent ---------------------------------------------------------------------------------------------------------------

    /// <summary>The active offers for a client (for all, or for one of its groups), each with its last run on this client.</summary>
    public static async Task<KioskOffer> OfferAsync(AppDbContext db, Client client, CancellationToken ct = default)
    {
        var items = await OffersForAsync(db, client.Id, ct);
        var ids = items.Select(i => i.Id).ToList();
        var lastIds = await db.JobRuns
            .Where(r => r.ClientId == client.Id && r.Job!.KioskItemId != null && ids.Contains(r.Job.KioskItemId.Value))
            .GroupBy(r => r.Job!.KioskItemId)
            .Select(g => g.Max(r => r.Id))
            .ToListAsync(ct);
        var last = await db.JobRuns.AsNoTracking().Include(r => r.Job).Where(r => lastIds.Contains(r.Id)).ToListAsync(ct);
        return new KioskOffer
        {
            Items = items.Select(i => new KioskOfferItem
            {
                Id = i.Id, Name = i.Name, Description = i.Description, Category = i.Category, Icon = i.Icon, Confirm = i.Confirm,
                Kind = i.Kind.ToString().ToLowerInvariant(),
                Last = last.FirstOrDefault(r => r.Job!.KioskItemId == i.Id) is { } run ? JobService.State(run) : null,
            }).ToList(),
        };
    }

    private static async Task<List<KioskItem>> OffersForAsync(AppDbContext db, long clientId, CancellationToken ct)
    {
        var groups = await db.ClientGroupMembers.Where(m => m.ClientId == clientId).Select(m => m.ClientGroupId).ToListAsync(ct);
        var active = await db.KioskItems.AsNoTracking().Where(i => i.IsActive).OrderBy(i => i.Category).ThenBy(i => i.SortOrder).ThenBy(i => i.Name).ToListAsync(ct);
        return active.Where(i => i.GroupIds.Count == 0 || i.GroupIds.Any(groups.Contains)).ToList();
    }

    /// <summary>
    /// Starts an offer on the client that asks (a person at it pressed Start): a job for this client only, made by MatHub itself. An offer that is still queued
    /// or running there is not started twice: its run is the answer.
    /// </summary>
    public static async Task<Outcome<long>> StartAsync(AppDbContext db, Client client, long itemId, string? user, CancellationToken ct = default)
    {
        if (client.Status != ClientStatus.Active)
        {
            return Outcome<long>.Denied();
        }

        var item = (await OffersForAsync(db, client.Id, ct)).FirstOrDefault(i => i.Id == itemId);
        if (item is null)
        {
            return Outcome<long>.Denied();
        }

        var open = await db.JobRuns.Where(r => r.ClientId == client.Id && r.Job!.KioskItemId == item.Id && (r.Status == RunStatus.Queued || r.Status == RunStatus.Running))
            .Select(r => (long?)r.Id).FirstOrDefaultAsync(ct);
        if (open is { } running)
        {
            return Outcome<long>.Success(running);
        }

        var request = item.Kind switch
        {
            KioskKind.Package => new JobRequest(JobKind.PackageInstall, null, item.PackageId, null, [client.Id], []),
            KioskKind.Sequence => new JobRequest(JobKind.Sequence, null, null, null, [client.Id], [], SequenceId: item.SequenceId),
            _ => new JobRequest(JobKind.Script, item.ScriptId, null, null, [client.Id], []),
        };
        System.Globalization.CultureInfo.CurrentUICulture = await Deployments.Planner.AdminCultureAsync(db, ct);
        var job = await JobService.CreateAsync(db, Actor.System, request, "kiosk", ct: ct, kioskItemId: item.Id);
        if (!job.Ok)
        {
            return job.NotAllowed ? Outcome<long>.Denied() : Outcome<long>.Fail(job.Error!);
        }

        var who = string.IsNullOrWhiteSpace(user) ? null : user.Trim().Length > 200 ? user.Trim()[..200] : user.Trim();
        ActivityLog.Add(db, Actor.System, "kiosk", "run", item.Name, who, client.Id);
        await db.SaveChangesAsync(ct);
        return Outcome<long>.Success(job.Value!.Runs.First().Id);
    }

    private static string? Cut(string? text, int max)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > max ? t[..max] : t;
    }
}
