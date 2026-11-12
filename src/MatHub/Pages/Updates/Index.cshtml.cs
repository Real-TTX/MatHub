using MatHub.Data;
using MatHub.Localization;
using MatHub.Services.Access;
using MatHub.Services.Packages;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Updates;

/// <summary>
/// Updates are versions, and a person approves them: what waits for a test or an approval, which new versions the catalog has that nobody took yet, which
/// clients still have an older version, and – only as information – what winget would update beyond the packages of MatHub.
/// </summary>
public sealed class IndexModel(AppDbContext db, WingetCatalog catalog) : AppPageModel
{
    public UpdateOverview Overview { get; private set; } = new([], [], [], new UpdateList([], 0, 0));

    public bool CanEdit => Me.Can(Permission.PackagesEdit);

    public string Zone => Me.TimeZone;

    public async Task<IActionResult> OnGetAsync()
    {
        if (await UpdateService.OverviewAsync(db, Me.Actor, HttpContext.RequestAborted) is not { } overview)
        {
            return NotFound();
        }

        Overview = overview;
        return Page();
    }

    /// <summary>Takes the newer version of the catalog into the package (downloaded and checked, not approved).</summary>
    public async Task<IActionResult> OnPostPrepareAsync(long package)
    {
        var outcome = await catalog.TakeNewerAsync(db, Me.Actor, package, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            FlashError(outcome.Error!);
            return LocalRedirect("/updates");
        }

        Flash(Text.T("Versions.Prepared", outcome.Value!.Version));
        return LocalRedirect($"/packages/{package}/versions");
    }
}
