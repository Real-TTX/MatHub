using MatHub.Data;
using MatHub.Localization;
using MatHub.Services.Access;
using MatHub.Services.Packages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Pages.Store;

/// <summary>A program of the store: its manifest (publisher, description, license, installers) and the ways it becomes a package of MatHub.</summary>
public sealed class ItemModel(AppDbContext db, WingetCatalog catalog) : AppPageModel
{
    public CatalogDetail Detail { get; private set; } = null!;

    public bool HasIcon { get; private set; }

    public bool CanEdit => Me.Can(Permission.PackagesEdit);

    /// <summary>The package has the version of the catalog already.</summary>
    public bool Known { get; private set; }

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
            Known = detail.PackageId is { } package && await db.PackageVersions.AnyAsync(v => v.PackageId == package && v.Version == detail.Version, HttpContext.RequestAborted);
            return Page();
        }
        catch (HttpRequestException ex)
        {
            FlashError(Text.T("Store.Error.Manifest", ex.Message));
            return LocalRedirect("/store?q=" + Uri.EscapeDataString(id ?? string.Empty));
        }
    }

    /// <summary>
    /// Adds the program: a new package with its current version approved and an installer for every platform (kept in MatHub, or with <paramref name="winget"/>
    /// winget with this version pinned); for a package there is already, the version of the catalog as a new version (not approved).
    /// </summary>
    public async Task<IActionResult> OnPostAddAsync(string id, bool winget)
    {
        var outcome = await catalog.AddAsync(db, Me.Actor, id, winget, HttpContext.RequestAborted);
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
        return LocalRedirect($"/packages/{outcome.Value.Id}/versions");
    }
}
