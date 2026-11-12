using MatHub.Localization;
using MatHub.Services;
using MatHub.Services.Agent;
using MatHub.Services.Mail;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Admin;

/// <summary>
/// The settings of the installation (registration, public address, how often the agents call and send their inventory, how long jobs are kept, the address
/// of the network boot) and what is in it; the same values as the platform API. An address a variable sets (<c>MAT_PUBLIC_URL</c>) is shown read-only.
/// </summary>
public sealed class SystemModel(InstanceService instance, MailService mail, AgentFiles agent) : AppPageModel
{
    [BindProperty] public bool RegistrationOpen { get; set; }

    [BindProperty] public string PublicUrl { get; set; } = string.Empty;

    /// <summary><c>MAT_PUBLIC_URL</c> sets the public address.</summary>
    public bool PublicUrlFixed { get; private set; }

    [BindProperty] public int AgentPollSeconds { get; set; } = 30;

    [BindProperty] public int InventoryHours { get; set; } = 6;

    [BindProperty] public int JobDays { get; set; } = 90;

    [BindProperty] public string BootUrl { get; set; } = string.Empty;

    public InstanceSnapshot Counts { get; private set; } = null!;

    public string Database { get; private set; } = string.Empty;

    public AgentFiles Agent => agent;

    /// <summary>Where the mail goes (MatSMTP), or null when the installation sends none.</summary>
    public string? MailText => mail.Describe(Languages.Current);

    public async Task OnGetAsync()
    {
        var s = await instance.HubSettingsAsync();
        RegistrationOpen = s.RegistrationOpen;
        PublicUrl = s.PublicUrl ?? string.Empty;
        AgentPollSeconds = s.AgentPollSeconds;
        InventoryHours = s.InventoryHours;
        JobDays = s.JobDays;
        BootUrl = s.BootUrl ?? string.Empty;
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // a fixed address is not posted (the field is read-only) and stays as it is
        var change = new HubSettingsChange(RegistrationOpen, PublicUrl, AgentPollSeconds, InventoryHours, JobDays, BootUrl);
        if (await instance.SaveSettingsAsync(change) is { } problem)
        {
            ModelState.AddModelError(problem.Field, problem.Message);
            await LoadAsync();
            return Page();
        }

        Flash(Text.T("Common.Saved"));
        return LocalRedirect("/Admin/System");
    }

    public async Task<IActionResult> OnPostCleanUpAsync()
    {
        var result = await instance.CleanUpAsync(HttpContext.RequestAborted);
        Flash(Text.T("System.CleanedUp", result.Sessions, result.Jobs, result.Rollouts));
        return LocalRedirect("/Admin/System");
    }

    private async Task LoadAsync()
    {
        Counts = await instance.SnapshotAsync();
        Database = instance.DatabaseName();
        var s = await instance.HubSettingsAsync();
        PublicUrlFixed = s.PublicUrlFixed;
        if (PublicUrlFixed)
        {
            PublicUrl = s.PublicUrl ?? string.Empty;
        }
    }
}
