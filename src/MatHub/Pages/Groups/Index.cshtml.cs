using MatHub.Data;
using MatHub.Localization;
using MatHub.Services;
using MatHub.Services.Access;
using MatHub.Services.Clients;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Groups;

/// <summary>The groups of clients: by hand or by a rule. They are the targets of jobs and assignments and the scope of roles.</summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public IReadOnlyList<GroupRow> Items { get; private set; } = [];

    public bool CanManage => Me.Can(Permission.GroupsManage) && Me.Actor.ReachesAll;

    public async Task<IActionResult> OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var kind = ListQuery.Text(Request, "kind");
        var sort = ListQuery.Text(Request, "sort") ?? "name";
        var desc = ListQuery.Descending(Request);
        var all = await GroupService.ListAsync(db, Me.Actor, null, HttpContext.RequestAborted);
        if (all is null)
        {
            return NotFound();
        }

        var rows = await GroupService.ListAsync(db, Me.Actor, search, HttpContext.RequestAborted) ?? [];
        if (Enum.TryParse<GroupKind>(kind, true, out var k))
        {
            rows = rows.Where(r => r.Kind == k).ToList();
        }

        rows = (sort, desc) switch
        {
            ("members", false) => rows.OrderBy(r => r.Members).ToList(),
            ("members", true) => rows.OrderByDescending(r => r.Members).ToList(),
            (_, true) => rows.OrderByDescending(r => r.Name).ToList(),
            _ => rows,
        };
        Items = rows;
        Toolbar = new ToolbarModel
        {
            Id = "groups",
            Action = "/groups",
            Label = Text.T("Groups.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Groups.Search"),
            Filters = [new FilterField("kind", Text.T("Groups.Kind"), [new("static", Text.T("Groups.Kind.Static")), new("dynamic", Text.T("Groups.Kind.Dynamic"))], kind)],
            SortOptions = [new("name", Text.T("Common.Name")), new("members", Text.T("Groups.Members"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "name",
            Total = all.Count,
            Matching = Items.Count,
            Noun = Text.T("Groups.Many"),
            NounOne = Text.T("Groups.One"),
        };
        return Page();
    }
}
