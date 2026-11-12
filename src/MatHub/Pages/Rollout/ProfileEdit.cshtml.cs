using System.Globalization;
using MatHub.Controls;
using MatHub.Data;
using MatHub.Localization;
using MatHub.Services.Access;
using MatHub.Services.Clients;
using MatHub.Services.Rollout;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Rollout;

/// <summary>
/// A rollout profile. The form asks what its kind needs (UI rule: a field that makes no sense for the kind is not shown): Windows the installation share, the
/// edition, the language, the time zone and the administrator's password; Linux its answer file; Custom an iPXE script. Passwords are never shown again (an
/// empty field keeps them).
/// </summary>
public sealed class ProfileEditModel(AppDbContext db, IDataProtectionProvider protection) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    [BindProperty] public string Name { get; set; } = string.Empty;

    [BindProperty] public string Description { get; set; } = string.Empty;

    [BindProperty] public ProfileKind Kind { get; set; } = ProfileKind.Windows;

    [BindProperty] public string BootImageId { get; set; } = string.Empty;

    [BindProperty] public string SourcePath { get; set; } = string.Empty;

    [BindProperty] public string SourceUser { get; set; } = string.Empty;

    [BindProperty] public string SourcePassword { get; set; } = string.Empty;

    [BindProperty] public int ImageIndex { get; set; } = 1;

    [BindProperty] public string ProductKey { get; set; } = string.Empty;

    [BindProperty] public string Locale { get; set; } = "de-DE";

    [BindProperty] public string TimeZone { get; set; } = "W. Europe Standard Time";

    [BindProperty] public string AdminPassword { get; set; } = string.Empty;

    [BindProperty] public string AnswerFile { get; set; } = string.Empty;

    [BindProperty] public string IpxeScript { get; set; } = string.Empty;

    [BindProperty] public bool InstallAgent { get; set; } = true;

    [BindProperty] public string GroupId { get; set; } = string.Empty;

    public bool HasSourcePassword { get; private set; }

    public bool HasAdminPassword { get; private set; }

    public bool IsNew => Id is null;

    public bool CanManage => Me.Can(Permission.RolloutManage);

    public List<Choice> Kinds { get; } = Enum.GetValues<ProfileKind>().Select(k => new Choice(k.ToString(), Text.T("Profiles.Kind." + k), Text.T("Profiles.Kind." + k + ".Hint"))).ToList();

    public List<Choice> Images { get; private set; } = [];

    public List<Choice> Groups { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadChoicesAsync();
        if (Id is not { } id)
        {
            return CanManage ? Page() : NotFound();
        }

        var p = await RolloutService.ProfileAsync(db, Me.Actor, id, HttpContext.RequestAborted);
        if (p is null)
        {
            return NotFound();
        }

        Name = p.Name;
        Description = p.Description ?? string.Empty;
        Kind = p.Kind;
        BootImageId = p.BootImageId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        SourcePath = p.SourcePath ?? string.Empty;
        SourceUser = p.SourceUser ?? string.Empty;
        ImageIndex = p.ImageIndex;
        ProductKey = p.ProductKey ?? string.Empty;
        Locale = p.Locale;
        TimeZone = p.TimeZone;
        AnswerFile = p.AnswerFile ?? string.Empty;
        IpxeScript = p.IpxeScript ?? string.Empty;
        InstallAgent = p.InstallAgent;
        GroupId = p.ClientGroupId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        HasSourcePassword = p.SourcePasswordProtected is not null;
        HasAdminPassword = p.AdminPasswordProtected is not null;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Kind == ProfileKind.Windows && IsNew && string.IsNullOrEmpty(AdminPassword))
        {
            ModelState.AddModelError(nameof(AdminPassword), Text.T("Profiles.AdminPassword.Required"));
            await LoadChoicesAsync();
            return Page();
        }

        var form = new ProfileForm(
            Name, Description, Kind, long.TryParse(BootImageId, out var image) ? image : null, SourcePath, SourceUser, SourcePassword, ImageIndex, ProductKey, Locale, TimeZone,
            AdminPassword, AnswerFile, IpxeScript, InstallAgent, long.TryParse(GroupId, out var g) ? g : null);
        var outcome = await RolloutService.SaveProfileAsync(db, Me.Actor, protection, Id, form, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            await LoadChoicesAsync();
            ModelState.AddModelError(FieldOf(outcome.Error!), outcome.Error!);
            return Page();
        }

        Flash(Text.T("Common.SavedNamed", outcome.Value!.Name));
        return LocalRedirect("/rollout/profiles");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var outcome = await RolloutService.DeleteProfileAsync(db, Me.Actor, Id ?? 0, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        Flash(Text.T("Common.DeletedNamed", outcome.Value!));
        return LocalRedirect("/rollout/profiles");
    }

    private string FieldOf(string error)
    {
        if (error == Text.T("Profiles.Image.Required"))
        {
            return nameof(BootImageId);
        }

        if (error == Text.T("Profiles.Source.Required"))
        {
            return nameof(SourcePath);
        }

        if (error == Text.T("Profiles.Ipxe.Required"))
        {
            return nameof(IpxeScript);
        }

        return error == Text.T("Profiles.Password.Characters") ? nameof(SourcePassword) : nameof(Name);
    }

    private async Task LoadChoicesAsync()
    {
        var ct = HttpContext.RequestAborted;
        Images = [new(string.Empty, Text.T("Profiles.NoImage")), .. (await RolloutService.ImageChoicesAsync(db, ct)).Select(i => new Choice(i.Id.ToString(CultureInfo.InvariantCulture), i.Name, Text.T("Images.Kind." + i.Kind)))];
        Groups = [new(string.Empty, Text.T("Enrollment.NoGroup")), .. (await GroupService.ChoicesAsync(db, Me.Actor, ct)).Where(x => x.Kind == GroupKind.Static).Select(x => new Choice(x.Id.ToString(CultureInfo.InvariantCulture), x.Name, null, x.Colour))];
    }
}
