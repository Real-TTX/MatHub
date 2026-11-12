using MatHub.Data;
using MatHub.Services;
using MatHub.Services.Access;
using MatHub.Services.Clients;
using MatHub.Services.Jobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatHub.Pages;

/// <summary>The start page: for a visitor what MatHub is, for a signed-in person the overview (clients, updates, jobs, what to do next).</summary>
public sealed class IndexModel(AccountService accounts, CurrentUser me, AppDbContext db, InstanceService instance) : PageModel
{
    public bool RegistrationOpen { get; private set; }

    public bool SignedIn => me.IsAuthenticated;

    public InstanceSnapshot? Counts { get; private set; }

    public IReadOnlyList<JobRow> RecentJobs { get; private set; } = [];

    public IReadOnlyList<ClientRow> Pending { get; private set; } = [];

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
        Counts = await DashboardAsync(ct);
        RecentJobs = (await JobService.ListAsync(db, me.Actor, new JobCriteria(null, null, null, null, 1, 6), ct))?.Items ?? [];
        if (me.Actor.Can(Permission.ClientsEnroll))
        {
            Pending = (await ClientService.ListAsync(db, me.Actor, new ClientCriteria(null, "pending", null, null, "created", true, 1, 5), ct))?.Items ?? [];
        }

        return Page();
    }

    /// <summary>The numbers of the clients the person reaches (an administrator: all of them).</summary>
    private async Task<InstanceSnapshot> DashboardAsync(CancellationToken ct)
    {
        if (me.IsAdmin)
        {
            return await instance.SnapshotAsync(ct);
        }

        var actor = me.Actor;
        var list = await ClientService.ListAsync(db, actor, new ClientCriteria(null, null, null, null, "name", false, 1, 1), ct);
        var online = await ClientService.ListAsync(db, actor, new ClientCriteria(null, "online", null, null, "name", false, 1, 1), ct);
        var pending = await ClientService.ListAsync(db, actor, new ClientCriteria(null, "pending", null, null, "name", false, 1, 1), ct);
        var updates = await ClientService.ListAsync(db, actor, new ClientCriteria(null, "updates", null, null, "name", false, 1, 1), ct);
        return new InstanceSnapshot(0, 0, list?.Total ?? 0, online?.Matching ?? 0, pending?.Matching ?? 0, updates?.Matching ?? 0, 0, 0, 0, 0, 0, null);
    }
}
