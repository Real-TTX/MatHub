using MatHub.Data;
using MatHub.Services.Jobs;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Jobs;

/// <summary>One run: the job on one client, its state, its times, its exit code and everything it wrote.</summary>
public sealed class RunModel(AppDbContext db) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }

    public JobRun Run { get; private set; } = null!;

    public string JobName { get; private set; } = string.Empty;

    public JobKind Kind { get; private set; }

    public string ClientName { get; private set; } = string.Empty;

    public string Zone => Me.TimeZone;

    public async Task<IActionResult> OnGetAsync()
    {
        if (await JobService.RunAsync(db, Me.Actor, Id, HttpContext.RequestAborted) is not { } found)
        {
            return NotFound();
        }

        (Run, JobName, Kind, ClientName) = found;
        return Page();
    }
}
