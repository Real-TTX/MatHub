using MatHub.Data;
using MatHub.Localization;
using MatHub.Services;
using MatHub.Services.Access;
using MatHub.Services.Clients;
using MatHub.Services.Jobs;
using MatHub.Services.Packages;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages;

/// <summary>The start page: for a visitor what MatHub is, for a signed-in person the overview (clients, updates, jobs, what to do next).</summary>
public sealed class IndexModel(AccountService accounts, CurrentUser me, AppDbContext db, InstanceService instance) : AppPageModel
{
    public bool RegistrationOpen { get; private set; }

    public bool SignedIn => me.IsAuthenticated;

    public ClientOverview? Clients { get; private set; }

    /// <summary>Failed runs of the last 24 hours and runs still open (administrators only).</summary>
    public (int Failed, int Open)? Runs { get; private set; }

    public IReadOnlyList<JobRow> RecentJobs { get; private set; } = [];

    public IReadOnlyList<ClientRow> Pending { get; private set; } = [];

    /// <summary>Versions that wait for a test or an approval.</summary>
    public IReadOnlyList<PendingRow> PendingVersions { get; private set; } = [];

    /// <summary>Clients with an older version of a package than the approved one.</summary>
    public int Outdated { get; private set; }

    public string Zone => me.TimeZone;

    public Actor Actor => me.Actor;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!me.IsAuthenticated)
        {
            RegistrationOpen = !await accounts.AnyUserAsync() || (await accounts.SettingsAsync()).RegistrationOpen;
            return Page();
        }

        var ct = HttpContext.RequestAborted;
        Clients = await ClientService.OverviewAsync(db, me.Actor, ct);
        if (me.IsAdmin)
        {
            var snapshot = await instance.SnapshotAsync(ct);
            Runs = (snapshot.FailedRuns24h, snapshot.OpenRuns);
        }

        RecentJobs = (await JobService.ListAsync(db, me.Actor, new JobCriteria(null, null, null, null, 1, 7), ct))?.Items ?? [];
        if (me.Actor.Can(Permission.ClientsEnroll))
        {
            Pending = (await ClientService.ListAsync(db, me.Actor, new ClientCriteria(null, "pending", null, null, "created", true, 1, 5), ct))?.Items ?? [];
        }

        if (await UpdateService.OverviewAsync(db, me.Actor, ct) is { } updates)
        {
            PendingVersions = updates.Pending.Take(5).ToList();
            Outdated = updates.Outdated.Sum(o => o.Outdated);
        }

        return Page();
    }

    /// <summary>Approves a client that waits (the business layer checks the permission and the reach).</summary>
    public async Task<IActionResult> OnPostApproveAsync(long id)
    {
        var outcome = await ClientService.SetStatusAsync(db, me.Actor, id, ClientStatus.Active, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        Flash(Text.T("Clients.Approved", outcome.Value!.Name));
        return LocalRedirect("/");
    }
}
