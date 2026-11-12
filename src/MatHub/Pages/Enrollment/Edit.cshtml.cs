using System.Globalization;
using MatHub.Controls;
using MatHub.Data;
using MatHub.Localization;
using MatHub.Services;
using MatHub.Services.Clients;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Enrollment;

/// <summary>An enrollment key: name, active, until when, how often, the group of the new clients, approval. A new key is shown once on the list.</summary>
public sealed class EditModel(AppDbContext db) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    [BindProperty] public string Name { get; set; } = string.Empty;

    [BindProperty] public bool IsActive { get; set; } = true;

    [BindProperty] public string Expires { get; set; } = string.Empty;

    [BindProperty] public int MaxUses { get; set; }

    [BindProperty] public bool AutoApprove { get; set; } = true;

    [BindProperty] public string GroupId { get; set; } = string.Empty;

    public string Prefix { get; private set; } = string.Empty;

    public int UseCount { get; private set; }

    public bool IsNew => Id is null;

    public List<Choice> Groups { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadChoicesAsync();
        if (Id is not { } id)
        {
            return Me.Can(MatHub.Services.Access.Permission.ClientsEnroll) ? Page() : NotFound();
        }

        var key = await EnrollmentService.GetAsync(db, Me.Actor, id, HttpContext.RequestAborted);
        if (key is null)
        {
            return NotFound();
        }

        Name = key.Name;
        IsActive = key.IsActive;
        Expires = key.ExpiresDate is { } e ? Dates.Iso(DateOnly.FromDateTime(Dates.Local(e, Me.TimeZone))) : string.Empty;
        MaxUses = key.MaxUses;
        AutoApprove = key.AutoApprove;
        GroupId = key.ClientGroupId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        Prefix = key.Prefix;
        UseCount = key.UseCount;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var form = new EnrollmentKeyForm(Name, IsActive, Dates.ParseIso(Expires), MaxUses, AutoApprove, long.TryParse(GroupId, out var g) ? g : null);
        var outcome = await EnrollmentService.SaveAsync(db, Me.Actor, Id, form, Me.TimeZone, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            await LoadChoicesAsync();
            ModelState.AddModelError(string.IsNullOrWhiteSpace(Name) ? nameof(Name) : nameof(GroupId), outcome.Error!);
            return Page();
        }

        if (outcome.Value.Plain is { } plain)
        {
            TempData["NewKey"] = plain;
        }
        else
        {
            Flash(Text.T("Common.SavedNamed", outcome.Value.Key.Name));
        }

        return LocalRedirect("/enrollment");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var outcome = await EnrollmentService.DeleteAsync(db, Me.Actor, Id ?? 0, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        Flash(Text.T("Common.DeletedNamed", outcome.Value!));
        return LocalRedirect("/enrollment");
    }

    private async Task LoadChoicesAsync()
    {
        var groups = await GroupService.ChoicesAsync(db, Me.Actor, HttpContext.RequestAborted);
        Groups = [new(string.Empty, Text.T("Enrollment.NoGroup")), .. groups.Where(x => x.Kind == GroupKind.Static).Select(x => new Choice(x.Id.ToString(CultureInfo.InvariantCulture), x.Name, null, x.Colour))];
    }
}
