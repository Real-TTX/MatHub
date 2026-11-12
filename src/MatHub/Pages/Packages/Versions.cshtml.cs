using MatHub.Data;
using MatHub.Localization;
using MatHub.Services;
using MatHub.Services.Access;
using MatHub.Services.Packages;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Packages;

/// <summary>
/// A package, tab Versions: every version with its installers per platform and its state (New, Test, Approved, Retired) and what moves it on: test it with the
/// pilot group, approve it (the assignments install it from then on), retire it. A new version: uploaded (MatHub reads the file and fills the fields), from the
/// catalog of winget (downloaded and checked), a script, or winget with a pinned version; a version takes an installer for each platform.
/// </summary>
[DisableRequestSizeLimit]
public sealed class VersionsModel(AppDbContext db, PackageFiles files, WingetCatalog catalog) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }

    /// <summary>The form: "version" (a new one), the id of a version (another installer for it), empty (none open).</summary>
    [BindProperty(SupportsGet = true)] public string Add { get; set; } = string.Empty;

    [BindProperty] public InstallerInput Input { get; set; } = new();

    [BindProperty] public IFormFile? Upload { get; set; }

    public Package Package { get; private set; } = null!;

    public IReadOnlyList<VersionView> Versions { get; private set; } = [];

    public string? Newer { get; private set; }

    public bool CanEdit => Me.Can(Permission.PackagesEdit);

    public bool CanDeploy => Me.Can(Permission.PackagesDeploy);

    public string Zone => Me.TimeZone;

    public PackageVersion? AddTo => long.TryParse(Add, out var v) ? Versions.FirstOrDefault(x => x.Version.Id == v)?.Version : null;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (Add == "version" && Versions.FirstOrDefault()?.Installers.FirstOrDefault() is { } last)
        {
            // a new version is like the one before: its kind, its platform, its switches
            (Input.Kind, Input.Architecture, Input.Arguments, Input.WingetId) = (last.Kind, last.Architecture, last.Arguments ?? string.Empty, last.WingetId ?? Package.WingetId ?? string.Empty);
        }

        return Page();
    }

    /// <summary>Step one: the file is staged and read; the page comes back with the fields filled.</summary>
    public async Task<IActionResult> OnPostCheckAsync()
    {
        if (!CanEdit || !await LoadAsync())
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
        Input.Take(InstallerAnalysis.Analyze(files.Staged(Input.Token)!, file.FileName), file.FileName);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        ModelState.Clear();
        var ct = HttpContext.RequestAborted;
        long versionId;
        if (AddTo is { } existing)
        {
            versionId = existing.Id;
        }
        else
        {
            var created = await PackageVersions.CreateAsync(db, Me.Actor, Id, Input.Version, "upload", null, ct);
            if (!created.Ok)
            {
                ModelState.AddModelError("Input.Version", created.Error ?? Text.T("Common.NotAllowed"));
                return Page();
            }

            versionId = created.Value!.Id;
        }

        var outcome = await AddInstallerAsync(versionId, ct);
        if (!outcome.Ok)
        {
            if (AddTo is null)
            {
                await PackageVersions.DeleteAsync(db, Me.Actor, files, versionId, ct);
            }

            ModelState.AddModelError("Input.Kind", outcome.Error ?? Text.T("Common.NotAllowed"));
            await LoadAsync();
            return Page();
        }

        Flash(Text.T("Versions.Saved", Input.Version.Length > 0 ? Input.Version : AddTo?.Version ?? string.Empty));
        return LocalRedirect($"/packages/{Id}/versions");
    }

    private async Task<Outcome<PackageInstaller>> AddInstallerAsync(long versionId, CancellationToken ct)
    {
        var staged = files.Staged(Input.Token);
        if (Input.Kind is PackageKind.Msi or PackageKind.Exe)
        {
            if (staged is not null)
            {
                await using var stream = System.IO.File.OpenRead(staged);
                var result = await PackageVersions.AddInstallerAsync(db, Me.Actor, files, versionId, Input.Form(), Path.GetFileName(staged), stream, ct);
                files.Forget(Input.Token);
                return result;
            }

            if (Upload is { Length: > 0 } file)
            {
                await using var stream = file.OpenReadStream();
                return await PackageVersions.AddInstallerAsync(db, Me.Actor, files, versionId, Input.Form(), file.FileName, stream, ct);
            }
        }

        return await PackageVersions.AddInstallerAsync(db, Me.Actor, files, versionId, Input.Form(), null, null, ct);
    }

    public async Task<IActionResult> OnPostStatusAsync(long version, VersionStatus status)
    {
        var outcome = await PackageVersions.SetStatusAsync(db, Me.Actor, version, status, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        if (outcome.Ok)
        {
            Flash(Text.T("Versions.Status." + status + ".Done", outcome.Value!.Version));
        }
        else
        {
            FlashError(outcome.Error!);
        }

        return LocalRedirect($"/packages/{Id}/versions");
    }

    public async Task<IActionResult> OnPostCatalogAsync()
    {
        var outcome = await catalog.TakeNewerAsync(db, Me.Actor, Id, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        if (outcome.Ok)
        {
            Flash(Text.T("Versions.Prepared", outcome.Value!.Version));
        }
        else
        {
            FlashError(outcome.Error!);
        }

        return LocalRedirect($"/packages/{Id}/versions");
    }

    public async Task<IActionResult> OnPostRemoveInstallerAsync(long installer)
    {
        var outcome = await PackageVersions.RemoveInstallerAsync(db, Me.Actor, files, installer, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            FlashError(outcome.Error!);
        }

        return LocalRedirect($"/packages/{Id}/versions");
    }

    public async Task<IActionResult> OnPostDeleteAsync(long version)
    {
        var outcome = await PackageVersions.DeleteAsync(db, Me.Actor, files, version, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        if (outcome.Ok)
        {
            Flash(Text.T("Common.DeletedNamed", outcome.Value!));
        }
        else
        {
            FlashError(outcome.Error!);
        }

        return LocalRedirect($"/packages/{Id}/versions");
    }

    private async Task<bool> LoadAsync()
    {
        var ct = HttpContext.RequestAborted;
        if (await PackageService.GetAsync(db, Me.Actor, Id, ct) is not { } package)
        {
            return false;
        }

        Package = package;
        Versions = await PackageVersions.ListAsync(db, Me.Actor, Id, ct) ?? [];
        Newer = await WingetCatalog.NewerAsync(db, package, ct);
        return true;
    }
}
