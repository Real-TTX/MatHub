using MatHub.Data;
using MatHub.Localization;
using MatHub.Services.Access;
using MatHub.Services.Packages;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Store;

/// <summary>The store: search the catalog of winget (loaded every day into MatHub) and open a program to make it a package.</summary>
public sealed class IndexModel(AppDbContext db, WingetCatalog catalog) : AppPageModel
{
    public const int Size = 30;

    public string? Search { get; private set; }

    public int PageNumber { get; private set; } = 1;

    public IReadOnlyList<CatalogRow> Items { get; private set; } = [];

    public int Matching { get; private set; }

    public CatalogState State { get; private set; } = new(null, 0, null);

    public bool CanEdit => Me.Can(Permission.PackagesEdit);

    public string Zone => Me.TimeZone;

    /// <summary>Searches to begin with.</summary>
    public IReadOnlyList<string> Popular { get; } = ["Firefox", "Chrome", "7-Zip", "VLC", "Notepad++", "Adobe Acrobat Reader", "Teams", "Zoom", "KeePass", "Visual Studio Code", "PDF", "Java"];

    public async Task<IActionResult> OnGetAsync(string? q, int page = 1)
    {
        if (!Me.Can(Permission.PackagesView))
        {
            return NotFound();
        }

        Search = q?.Trim();
        PageNumber = Math.Clamp(page, 1, 50);
        State = catalog.State();
        if (!string.IsNullOrWhiteSpace(Search))
        {
            // the pages so far and the next one, so "more" keeps what is there
            var found = await WingetCatalog.SearchAsync(db, Me.Actor, Search, 1, Size * PageNumber, HttpContext.RequestAborted);
            (Items, Matching) = found ?? ([], 0);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostRefreshAsync()
    {
        if (!CanEdit)
        {
            return NotFound();
        }

        var state = await catalog.RefreshAsync(db, HttpContext.RequestAborted);
        if (state.Error is null)
        {
            Flash(Text.T("Store.Loaded", state.Programs));
        }
        else
        {
            FlashError(Text.T("Store.LoadFailed") + " " + state.Error);
        }

        return LocalRedirect("/store");
    }
}
