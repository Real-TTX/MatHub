using MatHub.Data;
using MatHub.Services.Access;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services;

public sealed record ActivityRow(DateTime Date, string? UserName, string Area, string Action, string? Target, string? Detail, long? ClientId);

/// <summary>The activity of the installation (Administration → Activity, and the tab of a client): who changed, ran or enrolled what. Added to the context, saved with the change.</summary>
public static class ActivityLog
{
    /// <summary>The areas of the filter.</summary>
    public static readonly string[] Areas = ["client", "group", "enrollment", "script", "package", "job", "rollout", "role", "user"];

    public static void Add(AppDbContext db, Actor actor, string area, string action, string? target, string? detail = null, long? clientId = null)
        => db.Activities.Add(new Activity
        {
            UserId = actor.UserId,
            ClientId = clientId,
            Area = area,
            Action = action,
            Target = Trim(target, 255),
            Detail = Trim(detail, 1000),
            OccurredDate = DateTime.UtcNow,
        });

    /// <summary>The activity of the installation, newest first (administrators only).</summary>
    public static async Task<(IReadOnlyList<ActivityRow> Rows, int Total)?> AllAsync(AppDbContext db, Actor actor, string? search, string? area, int page, int pageSize, CancellationToken ct = default)
    {
        if (!actor.IsAdmin)
        {
            return null;
        }

        var q = db.Activities.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = $"%{search.Trim()}%";
            q = q.Where(a => EF.Functions.ILike(a.Target ?? string.Empty, s) || EF.Functions.ILike(a.Detail ?? string.Empty, s));
        }

        if (!string.IsNullOrEmpty(area))
        {
            q = q.Where(a => a.Area == area);
        }

        var total = await q.CountAsync(ct);
        var rows = await Rows(db, q.OrderByDescending(a => a.OccurredDate).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(ct);
        return (rows, total);
    }

    /// <summary>The activity of one client (the tab of the client).</summary>
    public static async Task<IReadOnlyList<ActivityRow>> ForClientAsync(AppDbContext db, Actor actor, long clientId, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ClientsView) || !await AccessRules.ReachesAsync(db, actor, clientId, ct))
        {
            return [];
        }

        return await Rows(db, db.Activities.AsNoTracking().Where(a => a.ClientId == clientId).OrderByDescending(a => a.OccurredDate).Take(100)).ToListAsync(ct);
    }

    private static IQueryable<ActivityRow> Rows(AppDbContext db, IQueryable<Activity> q)
        => q.Select(a => new ActivityRow(
            a.OccurredDate,
            db.Users.IgnoreQueryFilters().Where(u => u.Id == a.UserId).Select(u => u.DisplayName).FirstOrDefault(),
            a.Area, a.Action, a.Target, a.Detail, a.ClientId));

    private static string? Trim(string? text, int max) => text is null ? null : text.Length <= max ? text : text[..max];
}
