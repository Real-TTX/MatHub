using System.Globalization;
using MatHub.Data;
using MatHub.Localization;
using MatHub.Services;
using MatHub.Services.Access;
using MatHub.Services.Clients;
using MatHub.Services.Packages;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Updates;

/// <summary>
/// The updates winget offers on the clients, program by program: on how many clients, which version comes. One click updates a program everywhere, another
/// everything on every client (or in a group).
/// </summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public UpdateList List { get; private set; } = new([], 0, 0);

    public long? GroupId { get; private set; }

    public bool CanDeploy => Me.Can(Permission.PackagesDeploy);

    public bool CanMakePackage => Me.Can(Permission.PackagesEdit);

    /// <summary>The packages by winget id (a program that is a package links to it).</summary>
    public IReadOnlyDictionary<string, long> Packages { get; private set; } = new Dictionary<string, long>();

    public async Task<IActionResult> OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var search = ListQuery.Text(Request, "q");
        GroupId = long.TryParse(ListQuery.Text(Request, "group"), out var g) ? g : null;
        var all = await UpdateService.ListAsync(db, Me.Actor, null, GroupId, ct);
        var list = await UpdateService.ListAsync(db, Me.Actor, search, GroupId, ct);
        if (list is null || all is null)
        {
            return NotFound();
        }

        List = list;
        Packages = await PackageService.ByWingetIdAsync(db, Me.Actor, ct);
        var groups = await GroupService.ChoicesAsync(db, Me.Actor, ct);
        Toolbar = new ToolbarModel
        {
            Id = "updates",
            Action = "/updates",
            Label = Text.T("Updates.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Updates.Search"),
            Filters = [new FilterField("group", Text.T("Groups.One"), groups.Select(x => new FilterOption(x.Id.ToString(CultureInfo.InvariantCulture), x.Name)).ToList(), GroupId?.ToString(CultureInfo.InvariantCulture))],
            SortOptions = [new("clients", Text.T("Updates.Col.Clients"))],
            Sort = "clients",
            Descending = true,
            DefaultSort = "clients",
            DefaultDescending = true,
            Total = all.Programs,
            Matching = list.Programs,
            Noun = Text.T("Updates.Many"),
            NounOne = Text.T("Updates.One"),
        };
        return Page();
    }

    /// <summary>One program everywhere (<paramref name="wingetId"/>), or everything (empty), on the clients of the group of the filter or on all.</summary>
    public async Task<IActionResult> OnPostAsync(string? wingetId, long? group)
    {
        var outcome = await UpdateService.UpdateAsync(db, Me.Actor, string.IsNullOrWhiteSpace(wingetId) ? null : wingetId, group, null, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            FlashError(outcome.Error!);
            return LocalRedirect("/updates");
        }

        Flash(Text.T("Jobs.Started", outcome.Value!.Name));
        return LocalRedirect($"/jobs/{outcome.Value.Id}");
    }
}
