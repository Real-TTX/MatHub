using MatHub.Services;
using MatHub.Localization;
using MatHub.Data;
using MatHub.Services.Access;
using MatHub.Services.Rollout;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Rollout;

/// <summary>The rollout profiles: how a computer is installed (Windows from a share, Linux with an answer file, an iPXE script of your own).</summary>
public sealed class ProfilesModel(AppDbContext db) : AppPageModel
{
    public IReadOnlyList<ProfileRow> Items { get; private set; } = [];

    public ToolbarModel Toolbar { get; private set; } = null!;

    public bool CanManage => Me.Can(Permission.RolloutManage);

    public async Task<IActionResult> OnGetAsync()
    {
        var items = await RolloutService.ProfilesAsync(db, Me.Actor, HttpContext.RequestAborted);
        if (items is null)
        {
            return NotFound();
        }

        var search = ListQuery.Text(Request, "q");
        var kind = ListQuery.Text(Request, "kind");
        var sort = ListQuery.Text(Request, "sort") ?? "name";
        var desc = ListQuery.Descending(Request);
        IEnumerable<ProfileRow> rows = items;
        if (!string.IsNullOrWhiteSpace(search))
        {
            rows = rows.Where(p => p.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) || (p.ImageName?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false));
        }

        if (Enum.TryParse<ProfileKind>(kind, true, out var k))
        {
            rows = rows.Where(p => p.Kind == k);
        }

        rows = (sort, desc) switch
        {
            ("tasks", false) => rows.OrderBy(p => p.Tasks),
            ("tasks", true) => rows.OrderByDescending(p => p.Tasks),
            (_, true) => rows.OrderByDescending(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => rows.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        Items = rows.ToList();
        Toolbar = new ToolbarModel
        {
            Id = "profiles",
            Action = "/rollout/profiles",
            Label = Text.T("Profiles.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Profiles.Search"),
            Filters = [new FilterField("kind", Text.T("Profiles.Kind"), Enum.GetValues<ProfileKind>().Select(x => new FilterOption(x.ToString(), Text.T("Profiles.Kind." + x))).ToList(), kind)],
            SortOptions = [new("name", Text.T("Common.Name")), new("tasks", Text.T("Rollout.Open"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "name",
            Total = items.Count,
            Matching = Items.Count,
            Noun = Text.T("Profiles.Many"),
            NounOne = Text.T("Profiles.One"),
        };
        return Page();
    }
}
