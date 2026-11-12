using MatHub.Data;
using MatHub.Services;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Clients;

/// <summary>A client, tab Activity: enrollment, approvals, changes and jobs of this client.</summary>
public sealed class ActivityModel(AppDbContext db) : ClientPageModel(db)
{
    public IReadOnlyList<ActivityRow> Items { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadClientAsync())
        {
            return NotFound();
        }

        Items = await ActivityLog.ForClientAsync(Db, Me.Actor, Id, HttpContext.RequestAborted);
        return Page();
    }
}
