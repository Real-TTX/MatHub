using MatHub.Data;
using MatHub.Localization;
using MatHub.Services.Access;
using MatHub.Services.Packages;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Store;

/// <summary>A program of the store: its manifest (publisher, description, license, installers) and the ways it becomes a package of MatHub.</summary>
public sealed class ItemModel(AppDbContext db, WingetCatalog catalog) : AppPageModel
{
    public CatalogDetail Detail { get; private set; } = null!;

    public bool HasIcon { get; private set; }

    public bool CanEdit => Me.Can(Permission.PackagesEdit);

    public async Task<IActionResult> OnGetAsync(string id)
    {
        try
        {
            if (await catalog.DetailAsync(db, Me.Actor, id ?? string.Empty, HttpContext.RequestAborted) is not { } detail)
            {
                return NotFound();
            }

            Detail = detail;
            HasIcon = detail.IconSha256 is { } sha && catalog.IconPath(sha) is not null;
            return Page();
        }
        catch (HttpRequestException ex)
        {
            FlashError(Text.T("Store.Error.Manifest", ex.Message));
            return LocalRedirect("/store?q=" + Uri.EscapeDataString(id ?? string.Empty));
        }
    }

    public async Task<IActionResult> OnPostWingetAsync(string id)
    {
        var outcome = await catalog.AddWingetAsync(db, Me.Actor, id, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            FlashError(outcome.Error!);
            return LocalRedirect("/store/item?id=" + Uri.EscapeDataString(id));
        }

        Flash(Text.T("Store.Added", outcome.Value!.Name));
        return LocalRedirect($"/packages/edit?id={outcome.Value.Id}");
    }

    public async Task<IActionResult> OnPostCacheAsync(string id, int installer)
    {
        var outcome = await catalog.CacheAsync(db, Me.Actor, id, installer, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            FlashError(outcome.Error!);
            return LocalRedirect("/store/item?id=" + Uri.EscapeDataString(id));
        }

        Flash(Text.T("Store.Cached", outcome.Value!.Name));
        return LocalRedirect($"/packages/edit?id={outcome.Value.Id}");
    }
}
