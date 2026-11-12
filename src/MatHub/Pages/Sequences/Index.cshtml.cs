using MatHub.Data;
using MatHub.Localization;
using MatHub.Services;
using MatHub.Services.Access;
using MatHub.Services.Jobs;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Sequences;

/// <summary>The sequences: scripts, packages and restarts one after the other; search, category, sort.</summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public IReadOnlyList<SequenceRow> Items { get; private set; } = [];

    public bool CanEdit => Me.Can(Permission.ScriptsEdit);

    public bool CanRun => Me.Can(Permission.ScriptsRun);

    public string Zone => Me.TimeZone;

    public async Task<IActionResult> OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var search = ListQuery.Text(Request, "q");
        var category = ListQuery.Text(Request, "category");
        var sort = ListQuery.Text(Request, "sort") ?? "name";
        var desc = ListQuery.Descending(Request);
        var all = await SequenceService.ListAsync(db, Me.Actor, null, null, "name", false, ct);
        if (all is null)
        {
            return NotFound();
        }

        Items = await SequenceService.ListAsync(db, Me.Actor, search, category, sort, desc, ct) ?? [];
        var categories = await SequenceService.CategoriesAsync(db, Me.Actor, ct);
        Toolbar = new ToolbarModel
        {
            Id = "sequences",
            Action = "/sequences",
            Label = Text.T("Sequences.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Sequences.Search"),
            Filters = [new FilterField("category", Text.T("Scripts.Category"), categories.Select(c => new FilterOption(c, c)).ToList(), category)],
            SortOptions = [new("name", Text.T("Common.Name")), new("category", Text.T("Scripts.Category")), new("updated", Text.T("Common.Updated"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "name",
            Total = all.Count,
            Matching = Items.Count,
            Noun = Text.T("Sequences.Many"),
            NounOne = Text.T("Sequences.One"),
        };
        return Page();
    }
}
