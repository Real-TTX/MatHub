using System.Globalization;
using MatHub.Controls;
using MatHub.Data;
using MatHub.Localization;
using MatHub.Services.Access;
using MatHub.Services.Clients;
using MatHub.Services.Packages;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Packages;

/// <summary>
/// A package, tab General: what it is (name, publisher, description), how MatHub sees it on a client and in which version (the detection), where new versions
/// come from (the winget id, prepared by themselves or not) and which group tests a version first (the pilot group). Its versions and installers are on the
/// tab Versions; a new package begins in the assistant (<c>/packages/new</c>).
/// </summary>
public sealed class EditModel(AppDbContext db, PackageFiles files, WingetCatalog catalog) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    [BindProperty] public string Name { get; set; } = string.Empty;

    [BindProperty] public string Publisher { get; set; } = string.Empty;

    [BindProperty] public string Description { get; set; } = string.Empty;

    [BindProperty] public string WingetId { get; set; } = string.Empty;

    [BindProperty] public DetectionKind Detection { get; set; } = DetectionKind.WingetId;

    [BindProperty] public string DetectionValue { get; set; } = string.Empty;

    [BindProperty] public bool AutoUpdate { get; set; } = true;

    [BindProperty] public string PilotGroup { get; set; } = string.Empty;

    public bool CanEdit => Me.Can(Permission.PackagesEdit);

    public List<Choice> Detections { get; } = Enum.GetValues<DetectionKind>().Select(d => new Choice(d.ToString(), Text.T("Packages.Detection." + d))).ToList();

    public List<Choice> GroupChoices { get; private set; } = [];

    public IReadOnlyList<VersionView> Versions { get; private set; } = [];

    public string? Newer { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id is not { } id)
        {
            // a new package begins in the assistant (with what other pages filled in)
            return LocalRedirect("/packages/new" + Request.QueryString);
        }

        var p = await PackageService.GetAsync(db, Me.Actor, id, HttpContext.RequestAborted);
        if (p is null)
        {
            return NotFound();
        }

        Name = p.Name;
        Publisher = p.Publisher ?? string.Empty;
        Description = p.Description ?? string.Empty;
        WingetId = p.WingetId ?? string.Empty;
        Detection = p.Detection;
        DetectionValue = p.DetectionValue ?? string.Empty;
        AutoUpdate = p.AutoUpdate;
        PilotGroup = p.PilotGroupId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        await LoadAsync(p);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var pilot = long.TryParse(PilotGroup, NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) ? g : (long?)null;
        var outcome = await PackageService.SaveAsync(db, Me.Actor, Id, new PackageForm(Name, Publisher, Description, WingetId, Detection, DetectionValue, AutoUpdate, pilot), HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            ModelState.AddModelError(string.IsNullOrWhiteSpace(Name) ? nameof(Name) : nameof(DetectionValue), outcome.Error!);
            if (Id is { } id && await PackageService.GetAsync(db, Me.Actor, id, HttpContext.RequestAborted) is { } p)
            {
                await LoadAsync(p);
            }

            return Page();
        }

        Flash(Text.T("Common.SavedNamed", outcome.Value!.Name));
        return LocalRedirect($"/packages/edit?id={outcome.Value.Id}");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var outcome = await PackageService.DeleteAsync(db, Me.Actor, files, Id ?? 0, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        Flash(Text.T("Common.DeletedNamed", outcome.Value!));
        return LocalRedirect("/packages");
    }

    private async Task LoadAsync(Package p)
    {
        var ct = HttpContext.RequestAborted;
        GroupChoices = (await GroupService.ChoicesAsync(db, Me.Actor, ct)).Select(x => new Choice(x.Id.ToString(CultureInfo.InvariantCulture), x.Name, null, x.Colour)).ToList();
        GroupChoices.Insert(0, new Choice(string.Empty, Text.T("Packages.Pilot.None")));
        Versions = await PackageVersions.ListAsync(db, Me.Actor, p.Id, ct) ?? [];
        Newer = await WingetCatalog.NewerAsync(db, p, ct);
        _ = catalog;
    }
}
