using MatHub.Data;
using MatHub.Localization;
using MatHub.Services;
using MatHub.Services.Access;
using MatHub.Services.Packages;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Packages;

/// <summary>The packages: winget, MSI, EXE and scripted installs, with their assignments and on how many clients they are found.</summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public IReadOnlyList<PackageRow> Items { get; private set; } = [];

    public bool CanEdit => Me.Can(Permission.PackagesEdit);

    public bool CanDeploy => Me.Can(Permission.PackagesDeploy);

    public async Task<IActionResult> OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var search = ListQuery.Text(Request, "q");
        var kind = ListQuery.Text(Request, "kind");
        var sort = ListQuery.Text(Request, "sort") ?? "name";
        var desc = ListQuery.Descending(Request);
        var all = await PackageService.ChoicesAsync(db, Me.Actor, ct);
        var items = await PackageService.ListAsync(db, Me.Actor, search, kind, sort, desc, ct);
        if (items is null)
        {
            return NotFound();
        }

        Items = items;
        Toolbar = new ToolbarModel
        {
            Id = "packages",
            Action = "/packages",
            Label = Text.T("Packages.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Packages.Search"),
            Filters = [new FilterField("kind", Text.T("Packages.Kind"), Enum.GetValues<PackageKind>().Select(k => new FilterOption(k.ToString().ToLowerInvariant(), Text.T("Packages.Kind." + k))).Append(new FilterOption("waiting", Text.T("Packages.Filter.Waiting"))).ToList(), kind)],
            SortOptions = [new("name", Text.T("Common.Name")), new("kind", Text.T("Packages.Kind")), new("created", Text.T("Common.Created"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "name",
            Total = all.Count,
            Matching = Items.Count,
            Noun = Text.T("Packages.Many"),
            NounOne = Text.T("Packages.One"),
        };
        return Page();
    }
}
