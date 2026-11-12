using MatHub.Data;
using MatHub.Services.Access;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services;

public sealed record SearchItem(string Title, string? Text, string Href, string Icon, string Kind, long Id);

public sealed record SearchGroup(string Name, IReadOnlyList<SearchItem> Items);

/// <summary>
/// The search of the whole hub (the field in the top bar, SHELL-20, and the page <c>/search</c>): clients (name, address, serial number, user), groups, scripts
/// and packages, each only with its permission and only the clients the person reaches.
/// </summary>
public static class SearchService
{
    public static async Task<IReadOnlyList<SearchGroup>> SearchAsync(AppDbContext db, Actor actor, string? query, int limit, CancellationToken ct = default)
    {
        var text = query?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var s = $"%{text}%";
        var groups = new List<SearchGroup>();
        if (actor.Can(Permission.ClientsView))
        {
            var clients = await AccessRules.Clients(db, actor).AsNoTracking()
                .Where(c => EF.Functions.ILike(c.Name, s) || EF.Functions.ILike(c.IpAddresses ?? string.Empty, s) || EF.Functions.ILike(c.SerialNumber ?? string.Empty, s)
                            || EF.Functions.ILike(c.LastUser ?? string.Empty, s) || EF.Functions.ILike(c.Tags, s))
                .OrderBy(c => c.Name).Take(limit).Select(c => new { c.Id, c.Name, c.OperatingSystem, c.LastUser }).ToListAsync(ct);
            Add(groups, "Search.Clients", clients.Select(c => new SearchItem(c.Name, string.Join(" · ", new[] { c.OperatingSystem is null ? null : Ui.Os(c.OperatingSystem), c.LastUser }.Where(x => x is not null)), $"/clients/{c.Id}", "monitor", "client", c.Id)));

            var found = await AccessRules.Groups(db, actor).AsNoTracking().Where(g => EF.Functions.ILike(g.Name, s)).OrderBy(g => g.Name).Take(limit)
                .Select(g => new { g.Id, g.Name, g.Description }).ToListAsync(ct);
            Add(groups, "Search.Groups", found.Select(g => new SearchItem(g.Name, g.Description, $"/groups/edit?id={g.Id}", "folder", "group", g.Id)));
        }

        if (actor.Can(Permission.ScriptsView))
        {
            var scripts = await db.Scripts.AsNoTracking().Where(x => EF.Functions.ILike(x.Name, s) || EF.Functions.ILike(x.Category ?? string.Empty, s))
                .OrderBy(x => x.Name).Take(limit).Select(x => new { x.Id, x.Name, x.Category, x.Editor }).ToListAsync(ct);
            Add(groups, "Search.Scripts", scripts.Select(x => new SearchItem(x.Name, x.Category, $"/scripts/edit?id={x.Id}", JobUi.EditorIcon(x.Editor), "script", x.Id)));
        }

        if (actor.Can(Permission.PackagesView))
        {
            var packages = await db.Packages.AsNoTracking().Where(x => EF.Functions.ILike(x.Name, s) || EF.Functions.ILike(x.WingetId ?? string.Empty, s) || EF.Functions.ILike(x.Publisher ?? string.Empty, s))
                .OrderBy(x => x.Name).Take(limit).Select(x => new { x.Id, x.Name, x.Publisher, x.Kind }).ToListAsync(ct);
            Add(groups, "Search.Packages", packages.Select(x => new SearchItem(x.Name, x.Publisher, $"/packages/edit?id={x.Id}", JobUi.PackageIcon(x.Kind), "package", x.Id)));
        }

        return groups;
    }

    private static void Add(List<SearchGroup> groups, string key, IEnumerable<SearchItem> items)
    {
        var list = items.ToList();
        if (list.Count > 0)
        {
            groups.Add(new SearchGroup(Localization.Text.T(key), list));
        }
    }

    public static void MapSearch(this WebApplication app)
    {
        app.MapGet("/live/search", async (string? q, int? limit, AppDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            var groups = await SearchAsync(db, me.Actor, q, Math.Clamp(limit ?? 8, 1, 20), ct);
            return Results.Ok(new
            {
                groups = groups.Select(g => new { name = g.Name, items = g.Items.Select(i => new { title = i.Title, text = i.Text, href = i.Href, icon = i.Icon, kind = i.Kind, id = i.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }) }),
            });
        });
    }
}
