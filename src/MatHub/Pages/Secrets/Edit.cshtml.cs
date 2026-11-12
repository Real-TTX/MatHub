using MatHub.Data;
using MatHub.Localization;
using MatHub.Services.Scripts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Secrets;

/// <summary>A secret: its name (the scripts name it), a user name that belongs to it, and the value, which is written but never shown again.</summary>
public sealed class EditModel(AppDbContext db, IDataProtectionProvider protection) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    [BindProperty] public string Name { get; set; } = string.Empty;

    [BindProperty] public string UserName { get; set; } = string.Empty;

    [BindProperty] public string Value { get; set; } = string.Empty;

    [BindProperty] public string Description { get; set; } = string.Empty;

    public bool IsNew => Id is null;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Me.Can(Services.Access.Permission.SecretsManage))
        {
            return NotFound();
        }

        if (Id is { } id)
        {
            var secret = await SecretService.GetAsync(db, Me.Actor, id, HttpContext.RequestAborted);
            if (secret is null)
            {
                return NotFound();
            }

            Name = secret.Name;
            UserName = secret.UserName ?? string.Empty;
            Description = secret.Description ?? string.Empty;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var outcome = await SecretService.SaveAsync(db, Me.Actor, protection, Id, new SecretForm(Name, Description, UserName, Value), HttpContext.RequestAborted);
        Value = string.Empty;
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            var aboutValue = outcome.Error == Text.T("Secrets.Error.Value") || outcome.Error == Text.T("Secrets.Error.Long");
            ModelState.AddModelError(aboutValue ? nameof(Value) : nameof(Name), outcome.Error!);
            return Page();
        }

        Flash(Text.T("Common.SavedNamed", outcome.Value!.Name));
        return LocalRedirect("/secrets");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var outcome = await SecretService.DeleteAsync(db, Me.Actor, Id ?? 0, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        Flash(Text.T("Common.DeletedNamed", outcome.Value!));
        return LocalRedirect("/secrets");
    }
}
