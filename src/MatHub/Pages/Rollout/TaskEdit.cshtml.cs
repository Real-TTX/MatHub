using System.Globalization;
using MatHub.Controls;
using MatHub.Data;
using MatHub.Localization;
using MatHub.Services.Access;
using MatHub.Services.Rollout;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Rollout;

/// <summary>A rollout task: the MAC address, the computer name and the profile; install it once more, cancel it, delete it.</summary>
public sealed class TaskEditModel(AppDbContext db) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    [BindProperty(SupportsGet = true)] public string Mac { get; set; } = string.Empty;

    [BindProperty] public string HostName { get; set; } = string.Empty;

    [BindProperty] public string ProfileId { get; set; } = string.Empty;

    public RolloutTask? Current { get; private set; }

    public List<Choice> Profiles { get; private set; } = [];

    public bool IsNew => Id is null;

    public bool CanManage => Me.Can(Permission.RolloutManage);

    public string Zone => Me.TimeZone;

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadChoicesAsync();
        if (Id is not { } id)
        {
            return CanManage ? Page() : NotFound();
        }

        Current = await RolloutService.TaskAsync(db, Me.Actor, id, HttpContext.RequestAborted);
        if (Current is null)
        {
            return NotFound();
        }

        Mac = Current.MacAddress;
        HostName = Current.HostName;
        ProfileId = Current.ProfileId.ToString(CultureInfo.InvariantCulture);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var form = new TaskForm(Mac, HostName, long.TryParse(ProfileId, out var p) ? p : 0);
        var outcome = await RolloutService.SaveTaskAsync(db, Me.Actor, Id, form, HttpContext.RequestAborted);
        if (outcome.NotAllowed && form.ProfileId != 0)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            await LoadChoicesAsync();
            if (Id is { } id)
            {
                Current = await RolloutService.TaskAsync(db, Me.Actor, id, HttpContext.RequestAborted);
            }

            ModelState.AddModelError(form.ProfileId == 0 ? nameof(ProfileId) : outcome.Error == Text.T("Rollout.Mac.Invalid") ? nameof(Mac) : nameof(HostName), outcome.Error ?? Text.T("Rollout.Profile.Required"));
            return Page();
        }

        Flash(Text.T("Common.SavedNamed", outcome.Value!.HostName));
        return LocalRedirect("/rollout");
    }

    public Task<IActionResult> OnPostAgainAsync() => StatusAsync(RolloutStatus.Waiting, "Rollout.Again.Done");

    public Task<IActionResult> OnPostCancelAsync() => StatusAsync(RolloutStatus.Cancelled, "Rollout.Cancelled");

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var outcome = await RolloutService.DeleteTaskAsync(db, Me.Actor, Id ?? 0, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        Flash(Text.T("Common.DeletedNamed", outcome.Value!));
        return LocalRedirect("/rollout");
    }

    private async Task<IActionResult> StatusAsync(RolloutStatus status, string message)
    {
        if (!await RolloutService.SetTaskStatusAsync(db, Me.Actor, Id ?? 0, status, HttpContext.RequestAborted))
        {
            return NotFound();
        }

        Flash(Text.T(message));
        return LocalRedirect($"/rollout/tasks/edit?id={Id}");
    }

    private async Task LoadChoicesAsync()
        => Profiles = (await RolloutService.ProfileChoicesAsync(db, HttpContext.RequestAborted)).Select(p => new Choice(p.Id.ToString(CultureInfo.InvariantCulture), p.Name)).ToList();
}
