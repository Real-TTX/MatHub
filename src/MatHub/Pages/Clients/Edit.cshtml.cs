using System.Globalization;
using MatHub.Controls;
using MatHub.Data;
using MatHub.Localization;
using MatHub.Services;
using MatHub.Services.Clients;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Clients;

/// <summary>
/// A client, tab General: what a person may change (description, tags, the static groups), its facts, and what can be done with it (approve, run something,
/// ask for an inventory, switch off, delete).
/// </summary>
public sealed class EditModel(AppDbContext db) : ClientPageModel(db)
{
    [BindProperty] public string Description { get; set; } = string.Empty;

    [BindProperty] public string Tags { get; set; } = string.Empty;

    /// <summary>The static groups, joined by ", " (the select with several choices).</summary>
    [BindProperty] public string Groups { get; set; } = string.Empty;

    public List<Choice> GroupChoices { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        Description = Client.Description ?? string.Empty;
        Tags = Client.Tags.Replace(",", ", ", StringComparison.Ordinal);
        Groups = string.Join(", ", Detail.Groups.Where(g => g.Kind == GroupKind.Static).Select(g => g.Id));
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var ids = Groups.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0).Where(id => id > 0).ToList();
        var outcome = await ClientService.SaveAsync(Db, Me.Actor, Id, new ClientForm(Description, Tags, ids), HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        Flash(Text.T("Common.SavedNamed", outcome.Value!.Name));
        return LocalRedirect($"/clients/{Id}");
    }

    public async Task<IActionResult> OnPostApproveAsync() => await StatusAsync(ClientStatus.Active, "Clients.Approved");

    public async Task<IActionResult> OnPostDisableAsync() => await StatusAsync(ClientStatus.Disabled, "Clients.Disabled");

    public async Task<IActionResult> OnPostInventoryAsync()
    {
        if (!await ClientService.RequestInventoryAsync(Db, Me.Actor, Id, HttpContext.RequestAborted))
        {
            return NotFound();
        }

        Flash(Text.T("Clients.InventoryRequested"));
        return LocalRedirect($"/clients/{Id}");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var outcome = await ClientService.DeleteAsync(Db, Me.Actor, Id, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        Flash(Text.T("Common.DeletedNamed", outcome.Value!));
        return LocalRedirect("/clients");
    }

    private async Task<IActionResult> StatusAsync(ClientStatus status, string message)
    {
        var outcome = await ClientService.SetStatusAsync(Db, Me.Actor, Id, status, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        Flash(Text.T(message, outcome.Value!.Name));
        return LocalRedirect($"/clients/{Id}");
    }

    private async Task<bool> LoadAsync()
    {
        if (!await LoadClientAsync())
        {
            return false;
        }

        var groups = await GroupService.ChoicesAsync(Db, Me.Actor, HttpContext.RequestAborted);
        GroupChoices = groups.Where(g => g.Kind == GroupKind.Static).Select(g => new Choice(g.Id.ToString(CultureInfo.InvariantCulture), g.Name, null, g.Colour)).ToList();
        return true;
    }
}
