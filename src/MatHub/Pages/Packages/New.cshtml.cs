using MatHub.Controls;
using MatHub.Data;
using MatHub.Localization;
using MatHub.Services;
using MatHub.Services.Access;
using MatHub.Services.Packages;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Packages;

/// <summary>
/// The assistant of a new package: from the store (the catalog of winget), from an installer of one's own (MatHub reads the MSI or EXE and fills in name,
/// publisher, version, platform, switches and detection), a script, or winget with a pinned version. The first version is approved at once when the person
/// says so; every later one goes through the tab Versions.
/// </summary>
[DisableRequestSizeLimit]
public sealed class NewModel(AppDbContext db, PackageFiles files) : AppPageModel
{
    /// <summary>store, file, script, winget.</summary>
    [BindProperty(SupportsGet = true)] public string Source { get; set; } = string.Empty;

    [BindProperty] public string Name { get; set; } = string.Empty;

    [BindProperty] public string Publisher { get; set; } = string.Empty;

    [BindProperty] public string Description { get; set; } = string.Empty;

    [BindProperty] public DetectionKind Detection { get; set; } = DetectionKind.DisplayName;

    [BindProperty] public string DetectionValue { get; set; } = string.Empty;

    [BindProperty] public bool Approve { get; set; } = true;

    [BindProperty] public InstallerInput Input { get; set; } = new();

    [BindProperty] public IFormFile? Upload { get; set; }

    public List<Choice> Detections { get; } = Enum.GetValues<DetectionKind>().Select(d => new Choice(d.ToString(), Text.T("Packages.Detection." + d))).ToList();

    public IActionResult OnGet()
    {
        if (!Me.Can(Permission.PackagesEdit))
        {
            return NotFound();
        }

        if (Source == "store")
        {
            return LocalRedirect("/store");
        }

        (Input.Kind, Detection) = Source switch
        {
            "script" => (PackageKind.Script, DetectionKind.DisplayName),
            "winget" => (PackageKind.Winget, DetectionKind.WingetId),
            _ => (PackageKind.Msi, DetectionKind.DisplayName),
        };
        Input.Architecture = Source is "script" or "winget" ? InstallerArchitecture.Neutral : InstallerArchitecture.X64;
        return Page();
    }

    /// <summary>Step one: the installer is staged and read; name, publisher, version, platform, switches and detection come back filled in.</summary>
    public async Task<IActionResult> OnPostCheckAsync()
    {
        if (!Me.Can(Permission.PackagesEdit))
        {
            return NotFound();
        }

        ModelState.Clear();
        if (Upload is not { Length: > 0 } file)
        {
            ModelState.AddModelError("Upload", Text.T("Versions.Error.NoFile"));
            return Page();
        }

        await using var stream = file.OpenReadStream();
        Input.Token = await files.StageAsync(file.FileName, stream, HttpContext.RequestAborted);
        var facts = InstallerAnalysis.Analyze(files.Staged(Input.Token)!, file.FileName);
        Input.Take(facts, file.FileName);
        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = facts.ProductName is { } product ? PackageService.CleanName(product) : PackageService.CleanName(Path.GetFileNameWithoutExtension(file.FileName));
        }

        if (string.IsNullOrWhiteSpace(Publisher) && facts.Manufacturer is not null)
        {
            Publisher = facts.Manufacturer;
        }

        if (string.IsNullOrWhiteSpace(DetectionValue))
        {
            (Detection, DetectionValue) = (DetectionKind.DisplayName, facts.ProductName is { } p ? PackageService.CleanName(p) : Name);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        ModelState.Clear();
        var ct = HttpContext.RequestAborted;
        if (Input.Kind == PackageKind.Winget && Detection == DetectionKind.WingetId && string.IsNullOrWhiteSpace(DetectionValue))
        {
            DetectionValue = Input.WingetId;
        }

        var saved = await PackageService.SaveAsync(db, Me.Actor, null,
            new PackageForm(Name, Publisher, Description, Input.Kind == PackageKind.Winget ? Input.WingetId : null, Detection, DetectionValue, Input.Kind == PackageKind.Winget), ct);
        if (saved.NotAllowed)
        {
            return NotFound();
        }

        if (!saved.Ok)
        {
            ModelState.AddModelError(nameof(Name), saved.Error!);
            return Page();
        }

        var package = saved.Value!;
        var version = await PackageVersions.CreateAsync(db, Me.Actor, package.Id, Input.Version, Source == "script" ? "script" : "upload", null, ct);
        Outcome<PackageInstaller> installer;
        if (!version.Ok)
        {
            installer = Outcome<PackageInstaller>.Fail(version.Error!);
        }
        else if (files.Staged(Input.Token) is { } staged && Input.Kind is PackageKind.Msi or PackageKind.Exe)
        {
            await using var stream = System.IO.File.OpenRead(staged);
            installer = await PackageVersions.AddInstallerAsync(db, Me.Actor, files, version.Value!.Id, Input.Form(), Path.GetFileName(staged), stream, ct);
            files.Forget(Input.Token);
        }
        else if (Upload is { Length: > 0 } file && Input.Kind is PackageKind.Msi or PackageKind.Exe)
        {
            await using var stream = file.OpenReadStream();
            installer = await PackageVersions.AddInstallerAsync(db, Me.Actor, files, version.Value!.Id, Input.Form(), file.FileName, stream, ct);
        }
        else
        {
            installer = await PackageVersions.AddInstallerAsync(db, Me.Actor, files, version.Value!.Id, Input.Form(), null, null, ct);
        }

        if (!installer.Ok)
        {
            // nothing half made stays behind
            await PackageService.DeleteAsync(db, Me.Actor, files, package.Id, ct);
            ModelState.AddModelError(version.Ok ? "Input.Kind" : "Input.Version", installer.Error!);
            return Page();
        }

        if (Approve)
        {
            await PackageVersions.SetStatusAsync(db, Me.Actor, version.Value!.Id, VersionStatus.Approved, ct);
        }

        Flash(Text.T("Common.CreatedNamed", package.Name));
        return LocalRedirect($"/packages/{package.Id}/versions");
    }
}
