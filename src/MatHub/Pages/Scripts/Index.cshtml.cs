using MatHub.Data;
using MatHub.Localization;
using MatHub.Services;
using MatHub.Services.Access;
using MatHub.Services.Scripts;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Scripts;

/// <summary>The scripts: written as code, from blocks or as a flow; search, category, editor.</summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public IReadOnlyList<ScriptRow> Items { get; private set; } = [];

    public bool CanEdit => Me.Can(Permission.ScriptsEdit);

    public bool CanRun => Me.Can(Permission.ScriptsRun);

    public string Zone => Me.TimeZone;

    public async Task<IActionResult> OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var search = ListQuery.Text(Request, "q");
        var category = ListQuery.Text(Request, "category");
        var editor = ListQuery.Text(Request, "editor");
        var sort = ListQuery.Text(Request, "sort") ?? "name";
        var desc = ListQuery.Descending(Request);
        var all = await ScriptService.ListAsync(db, Me.Actor, null, null, null, "name", false, ct);
        if (all is null)
        {
            return NotFound();
        }

        Items = await ScriptService.ListAsync(db, Me.Actor, search, category, editor, sort, desc, ct) ?? [];
        var categories = await ScriptService.CategoriesAsync(db, Me.Actor, ct);
        Toolbar = new ToolbarModel
        {
            Id = "scripts",
            Action = "/scripts",
            Label = Text.T("Scripts.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Scripts.Search"),
            Filters =
            [
                new FilterField("category", Text.T("Scripts.Category"), categories.Select(c => new FilterOption(c, c)).ToList(), category),
                new FilterField("editor", Text.T("Scripts.Editor"), Enum.GetValues<ScriptEditor>().Select(e => new FilterOption(e.ToString().ToLowerInvariant(), Text.T("Scripts.Editor." + e))).ToList(), editor),
            ],
            SortOptions = [new("name", Text.T("Common.Name")), new("category", Text.T("Scripts.Category")), new("updated", Text.T("Common.Updated"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "name",
            Total = all.Count,
            Matching = Items.Count,
            Noun = Text.T("Scripts.Many"),
            NounOne = Text.T("Scripts.One"),
        };
        return Page();
    }
}
