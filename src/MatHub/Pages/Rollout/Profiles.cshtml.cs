using MatHub.Data;
using MatHub.Services.Access;
using MatHub.Services.Rollout;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Rollout;

/// <summary>The rollout profiles: how a computer is installed (Windows from a share, Linux with an answer file, an iPXE script of your own).</summary>
public sealed class ProfilesModel(AppDbContext db) : AppPageModel
{
    public IReadOnlyList<ProfileRow> Items { get; private set; } = [];

    public bool CanManage => Me.Can(Permission.RolloutManage);

    public async Task<IActionResult> OnGetAsync()
    {
        var items = await RolloutService.ProfilesAsync(db, Me.Actor, HttpContext.RequestAborted);
        if (items is null)
        {
            return NotFound();
        }

        Items = items;
        return Page();
    }
}
