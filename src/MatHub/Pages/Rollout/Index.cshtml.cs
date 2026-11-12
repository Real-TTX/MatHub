using MatHub.Data;
using MatHub.Localization;
using MatHub.Services;
using MatHub.Services.Access;
using MatHub.Services.Rollout;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Rollout;

/// <summary>The rollout tasks: which computer (MAC address) is installed with which profile at its next network boot, and how far it got.</summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public IReadOnlyList<TaskRow> Items { get; private set; } = [];

    public bool CanManage => Me.Can(Permission.RolloutManage);

    public string Zone => Me.TimeZone;

    public bool Busy => Items.Any(t => t.Status is RolloutStatus.Waiting or RolloutStatus.Installing);

    public async Task<IActionResult> OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var state = ListQuery.Text(Request, "state");
        var all = await RolloutService.TasksAsync(db, Me.Actor, null, null, HttpContext.RequestAborted);
        var items = await RolloutService.TasksAsync(db, Me.Actor, search, state, HttpContext.RequestAborted);
        if (items is null || all is null)
        {
            return NotFound();
        }

        Items = items;
        Toolbar = new ToolbarModel
        {
            Id = "rollout",
            Action = "/rollout",
            Label = Text.T("Rollout.Tasks"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Rollout.Search"),
            Filters = [new FilterField("state", Text.T("Common.Status"), [new("open", Text.T("Rollout.Open")), new("done", Text.T("Rollout.Status.Completed")), new("cancelled", Text.T("Rollout.Status.Cancelled"))], state)],
            SortOptions = [new("date", Text.T("Common.Created"))],
            Sort = "date",
            Descending = true,
            DefaultSort = "date",
            DefaultDescending = true,
            Total = all.Count,
            Matching = Items.Count,
            Noun = Text.T("Rollout.Many"),
            NounOne = Text.T("Rollout.One"),
        };
        return Page();
    }
}
