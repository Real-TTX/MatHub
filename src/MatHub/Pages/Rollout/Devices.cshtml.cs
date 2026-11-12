using MatHub.Data;
using MatHub.Localization;
using MatHub.Services;
using MatHub.Services.Access;
using MatHub.Services.Rollout;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Rollout;

/// <summary>The computers that booted from the network (seen by iPXE): what their firmware said, whether they are a client already, and a task for each.</summary>
public sealed class DevicesModel(AppDbContext db) : AppPageModel
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public IReadOnlyList<DeviceRow> Items { get; private set; } = [];

    public bool CanManage => Me.Can(Permission.RolloutManage);

    public string Zone => Me.TimeZone;

    public async Task<IActionResult> OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var all = await RolloutService.DevicesAsync(db, Me.Actor, null, HttpContext.RequestAborted);
        var items = await RolloutService.DevicesAsync(db, Me.Actor, search, HttpContext.RequestAborted);
        if (items is null || all is null)
        {
            return NotFound();
        }

        Items = items;
        Toolbar = new ToolbarModel
        {
            Id = "devices",
            Action = "/rollout/devices",
            Label = Text.T("Devices.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Devices.Search"),
            SortOptions = [new("boot", Text.T("Devices.LastBoot"))],
            Sort = "boot",
            Descending = true,
            DefaultSort = "boot",
            DefaultDescending = true,
            Total = all.Count,
            Matching = Items.Count,
            Noun = Text.T("Devices.Many"),
            NounOne = Text.T("Devices.One"),
        };
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(long device)
    {
        var outcome = await RolloutService.DeleteDeviceAsync(db, Me.Actor, device, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        Flash(Text.T("Common.DeletedNamed", outcome.Value!));
        return LocalRedirect("/rollout/devices");
    }
}
