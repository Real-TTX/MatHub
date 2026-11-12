using MatHub.Data;
using MatHub.Services.Access;
using MatHub.Services.Rollout;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Rollout;

/// <summary>The boot images: Windows PE (boot.wim, BCD, boot.sdi) and Linux (kernel, initrd), with which of their files are there.</summary>
public sealed class ImagesModel(AppDbContext db, NetbootFiles files) : AppPageModel
{
    public IReadOnlyList<ImageRow> Items { get; private set; } = [];

    public bool CanManage => Me.Can(Permission.RolloutManage);

    public async Task<IActionResult> OnGetAsync()
    {
        var items = await RolloutService.ImagesAsync(db, Me.Actor, files, HttpContext.RequestAborted);
        if (items is null)
        {
            return NotFound();
        }

        Items = items;
        return Page();
    }
}
