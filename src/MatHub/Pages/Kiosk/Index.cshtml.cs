using MatHub.Data;
using MatHub.Localization;
using MatHub.Services;
using MatHub.Services.Kiosk;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Kiosk;

/// <summary>The offers of the kiosk: what the people at the computers start themselves; search, category, sort.</summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public IReadOnlyList<KioskRow> Items { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var search = ListQuery.Text(Request, "q");
        var category = ListQuery.Text(Request, "category");
        var sort = ListQuery.Text(Request, "sort") ?? "order";
        var desc = ListQuery.Descending(Request);
        var all = await KioskService.ListAsync(db, Me.Actor, null, null, "order", false, ct);
        if (all is null)
        {
            return NotFound();
        }

        Items = await KioskService.ListAsync(db, Me.Actor, search, category, sort, desc, ct) ?? [];
        var categories = await KioskService.CategoriesAsync(db, Me.Actor, ct);
        Toolbar = new ToolbarModel
        {
            Id = "kiosk",
            Action = "/kiosk",
            Label = Text.T("Kiosk.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Kiosk.Search"),
            Filters = [new FilterField("category", Text.T("Kiosk.Field.Category"), categories.Select(c => new FilterOption(c, c)).ToList(), category)],
            SortOptions = [new("order", Text.T("Kiosk.Field.SortOrder")), new("name", Text.T("Common.Name")), new("category", Text.T("Kiosk.Field.Category"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "order",
            Total = all.Count,
            Matching = Items.Count,
            Noun = Text.T("Kiosk.Many"),
            NounOne = Text.T("Kiosk.One"),
        };
        return Page();
    }
}
