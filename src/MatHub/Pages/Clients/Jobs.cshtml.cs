using MatHub.Data;
using MatHub.Services.Jobs;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Clients;

/// <summary>A client, tab Jobs: the runs on this client, newest first, each with its job and its result.</summary>
public sealed class JobsModel(AppDbContext db) : ClientPageModel(db)
{
    public IReadOnlyList<ClientRunRow> Runs { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadClientAsync() || !CanSeeJobs)
        {
            return NotFound();
        }

        Runs = await JobService.RunsOfClientAsync(Db, Me.Actor, Id, HttpContext.RequestAborted);
        return Page();
    }
}
