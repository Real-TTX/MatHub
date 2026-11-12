using MatHub.Controls;
using MatHub.Data;
using MatHub.Localization;
using MatHub.Services;
using MatHub.Services.Access;
using MatHub.Services.Packages;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Packages;

/// <summary>
/// A package, tab General. What the form asks depends on the kind (UI rule: a field that makes no sense for the kind is not shown): the winget id and automatic
/// updates for winget, the installer and its arguments for MSI and EXE, the scripts for a scripted package; then how MatHub sees that it is installed.
/// </summary>
[DisableRequestSizeLimit]
public sealed class EditModel(AppDbContext db, PackageFiles files) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    [BindProperty] public string Name { get; set; } = string.Empty;

    [BindProperty] public string Publisher { get; set; } = string.Empty;

    [BindProperty] public string Description { get; set; } = string.Empty;

    [BindProperty] public PackageKind Kind { get; set; } = PackageKind.Winget;

    [BindProperty] public string Version { get; set; } = string.Empty;

    [BindProperty] public string WingetId { get; set; } = string.Empty;

    [BindProperty] public string InstallArguments { get; set; } = string.Empty;

    [BindProperty] public string UninstallCommand { get; set; } = string.Empty;

    [BindProperty] public string InstallScript { get; set; } = string.Empty;

    [BindProperty] public DetectionKind Detection { get; set; } = DetectionKind.WingetId;

    [BindProperty] public string DetectionValue { get; set; } = string.Empty;

    [BindProperty] public bool AutoUpdate { get; set; } = true;

    [BindProperty] public IFormFile? Installer { get; set; }

    public string? FileName { get; private set; }

    public long? FileSize { get; private set; }

    public string? FileSha256 { get; private set; }

    public bool IsNew => Id is null;

    public bool CanEdit => Me.Can(Permission.PackagesEdit);

    public List<Choice> Kinds { get; } = Enum.GetValues<PackageKind>().Select(k => new Choice(k.ToString(), Text.T("Packages.Kind." + k), Text.T("Packages.Kind." + k + ".Hint"))).ToList();

    public List<Choice> Detections { get; } = Enum.GetValues<DetectionKind>().Select(d => new Choice(d.ToString(), Text.T("Packages.Detection." + d))).ToList();

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id is not { } id)
        {
            if (!Me.Can(Permission.PackagesEdit))
            {
                return NotFound();
            }

            // filled in from a program a client has (software tab, updates)
            var q = Request.Query;
            if (q["winget"].ToString() is { Length: > 0 and <= 200 } winget)
            {
                Kind = PackageKind.Winget;
                WingetId = winget.Trim();
                Name = q["name"].ToString() is { Length: > 0 and <= 200 } n ? PackageService.CleanName(n) : WingetId;
                Publisher = q["publisher"].ToString() is { Length: <= 200 } pub ? pub.Trim() : string.Empty;
                Detection = DetectionKind.WingetId;
                AutoUpdate = true;
            }

            return Page();
        }

        var p = await PackageService.GetAsync(db, Me.Actor, id, HttpContext.RequestAborted);
        if (p is null)
        {
            return NotFound();
        }

        Name = p.Name;
        Publisher = p.Publisher ?? string.Empty;
        Description = p.Description ?? string.Empty;
        Kind = p.Kind;
        Version = p.Version ?? string.Empty;
        WingetId = p.WingetId ?? string.Empty;
        InstallArguments = p.InstallArguments ?? string.Empty;
        UninstallCommand = p.UninstallCommand ?? string.Empty;
        InstallScript = p.InstallScript ?? string.Empty;
        Detection = p.Detection;
        DetectionValue = p.DetectionValue ?? string.Empty;
        AutoUpdate = p.AutoUpdate;
        ShowFile(p);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var form = new PackageForm(Name, Publisher, Description, Kind, Version, WingetId, InstallArguments, UninstallCommand, InstallScript, Detection, DetectionValue, AutoUpdate);
        var outcome = await PackageService.SaveAsync(db, Me.Actor, Id, form, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            ModelState.AddModelError(string.IsNullOrWhiteSpace(Name) ? nameof(Name) : Kind == PackageKind.Winget ? nameof(WingetId) : nameof(InstallScript), outcome.Error!);
            await ShowStoredFileAsync();
            return Page();
        }

        var package = outcome.Value!;
        if (Installer is { Length: > 0 } file && Kind is PackageKind.Msi or PackageKind.Exe)
        {
            await using var stream = file.OpenReadStream();
            var upload = await PackageService.UploadAsync(db, Me.Actor, files, package.Id, file.FileName, stream, HttpContext.RequestAborted);
            if (!upload.Ok)
            {
                FlashError(upload.Error ?? Text.T("Common.NotAllowed"));
                return LocalRedirect($"/packages/edit?id={package.Id}");
            }
        }

        Flash(Text.T("Common.SavedNamed", package.Name));
        return LocalRedirect($"/packages/edit?id={package.Id}");
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

    private async Task ShowStoredFileAsync()
    {
        if (Id is { } id && await PackageService.GetAsync(db, Me.Actor, id, HttpContext.RequestAborted) is { } p)
        {
            ShowFile(p);
        }
    }

    private void ShowFile(Package p)
    {
        FileName = p.FileName;
        FileSize = p.FileSize;
        FileSha256 = p.FileSha256;
    }
}
