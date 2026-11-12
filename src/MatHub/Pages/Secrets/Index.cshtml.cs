using MatHub.Data;
using MatHub.Localization;
using MatHub.Services;
using MatHub.Services.Scripts;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Secrets;

/// <summary>The secrets of the scripts: their names, user names and where they are used; the values are never shown.</summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public IReadOnlyList<SecretRow> Items { get; private set; } = [];

    public string Zone => Me.TimeZone;

    public async Task<IActionResult> OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var search = ListQuery.Text(Request, "q");
        var all = await SecretService.ListAsync(db, Me.Actor, null, ct);
        if (all is null)
        {
            return NotFound();
        }

        Items = await SecretService.ListAsync(db, Me.Actor, search, ct) ?? [];
        Toolbar = new ToolbarModel
        {
            Id = "secrets",
            Action = "/secrets",
            Label = Text.T("Secrets.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Secrets.Search"),
            Total = all.Count,
            Matching = Items.Count,
            Noun = Text.T("Secrets.Many"),
            NounOne = Text.T("Secrets.One"),
        };
        return Page();
    }
}
