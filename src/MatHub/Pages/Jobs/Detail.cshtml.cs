using MatHub.Data;
using MatHub.Localization;
using MatHub.Services.Access;
using MatHub.Services.Jobs;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Jobs;

/// <summary>One job: what ran, by whom, and its run on every client (the clients the person reaches) with the result; cancel what has not started, run the failed ones again.</summary>
public sealed class DetailModel(AppDbContext db) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }

    public JobDetail Detail { get; private set; } = null!;

    public string Zone => Me.TimeZone;

    public bool Busy => Detail.Runs.Any(r => r.Status is RunStatus.Queued or RunStatus.Running);

    public bool CanCancel => Me.Can(Permission.JobsManage) && Detail.Runs.Any(r => r.Status == RunStatus.Queued);

    public bool CanRetry => Me.Can(JobService.PermissionFor(Detail.Job.Kind)) && Detail.Runs.Any(r => r.Status is RunStatus.Failed or RunStatus.Cancelled);

    public async Task<IActionResult> OnGetAsync()
    {
        if (await JobService.GetAsync(db, Me.Actor, Id, HttpContext.RequestAborted) is not { } detail)
        {
            return NotFound();
        }

        Detail = detail;
        return Page();
    }

    public async Task<IActionResult> OnPostCancelAsync()
    {
        var outcome = await JobService.CancelAsync(db, Me.Actor, Id, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        Flash(Text.T("Jobs.Cancelled", outcome.Value));
        return LocalRedirect($"/jobs/{Id}");
    }

    public async Task<IActionResult> OnPostRetryAsync()
    {
        var outcome = await JobService.RetryAsync(db, Me.Actor, Id, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            FlashError(outcome.Error!);
            return LocalRedirect($"/jobs/{Id}");
        }

        Flash(Text.T("Jobs.Started", outcome.Value!.Name));
        return LocalRedirect($"/jobs/{outcome.Value.Id}");
    }
}
