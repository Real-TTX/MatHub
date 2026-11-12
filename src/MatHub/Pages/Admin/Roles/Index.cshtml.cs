using MatHub.Services;
using MatHub.Localization;
using MatHub.Data;
using MatHub.Services.Access;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Admin.Roles;

/// <summary>The roles: sets of permissions for all clients or for groups. The built-in ones can be changed, not deleted (MatIdentity names them).</summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    public IReadOnlyList<RoleRow> Items { get; private set; } = [];

    public ToolbarModel Toolbar { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync()
    {
        var items = await RoleService.ListAsync(db, Me.Actor, HttpContext.RequestAborted);
        if (items is null)
        {
            return NotFound();
        }

        var search = ListQuery.Text(Request, "q");
        var scope = ListQuery.Text(Request, "scope");
        var sort = ListQuery.Text(Request, "sort") ?? "name";
        var desc = ListQuery.Descending(Request);
        IEnumerable<RoleRow> rows = items;
        if (!string.IsNullOrWhiteSpace(search))
        {
            rows = rows.Where(r => r.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) || (r.Description?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false));
        }

        rows = scope switch
        {
            "all" => rows.Where(r => r.AllClients),
            "groups" => rows.Where(r => !r.AllClients),
            _ => rows,
        };
        rows = (sort, desc) switch
        {
            ("users", false) => rows.OrderBy(r => r.Users),
            ("users", true) => rows.OrderByDescending(r => r.Users),
            (_, true) => rows.OrderByDescending(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        Items = rows.ToList();
        Toolbar = new ToolbarModel
        {
            Id = "roles",
            Action = "/Admin/Roles",
            Label = Text.T("Roles.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Roles.Search"),
            Filters = [new FilterField("scope", Text.T("Roles.Scope"), [new("all", Text.T("Roles.AllClients")), new("groups", Text.T("Roles.SomeGroups"))], scope)],
            SortOptions = [new("name", Text.T("Common.Name")), new("users", Text.T("Users.Title"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "name",
            Total = items.Count,
            Matching = Items.Count,
            Noun = Text.T("Roles.Many"),
            NounOne = Text.T("Roles.One"),
        };
        return Page();
    }
}
