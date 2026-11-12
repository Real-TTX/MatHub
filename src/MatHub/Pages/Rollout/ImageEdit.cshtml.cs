using MatHub.Controls;
using MatHub.Data;
using MatHub.Localization;
using MatHub.Services.Access;
using MatHub.Services.Rollout;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Rollout;

/// <summary>
/// A boot image: name, kind (chosen once), the kernel arguments of a Linux image, and its files: one upload per file (boot.wim is large; the upload streams
/// into the data volume).
/// </summary>
[DisableRequestSizeLimit]
public sealed class ImageEditModel(AppDbContext db, NetbootFiles files) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    [BindProperty] public string Name { get; set; } = string.Empty;

    [BindProperty] public string Description { get; set; } = string.Empty;

    [BindProperty] public BootImageKind Kind { get; set; } = BootImageKind.WindowsPe;

    [BindProperty] public string KernelArguments { get; set; } = string.Empty;

    public IReadOnlyList<(string File, long? Size)> Files { get; private set; } = [];

    public bool IsNew => Id is null;

    public bool CanManage => Me.Can(Permission.RolloutManage);

    public List<Choice> Kinds { get; } = Enum.GetValues<BootImageKind>().Select(k => new Choice(k.ToString(), Text.T("Images.Kind." + k), Text.T("Images.Kind." + k + ".Hint"))).ToList();

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id is not { } id)
        {
            return CanManage ? Page() : NotFound();
        }

        return await LoadAsync(id, fill: true) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var outcome = await RolloutService.SaveImageAsync(db, Me.Actor, Id, new ImageForm(Name, Description, Kind, KernelArguments), HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            ModelState.AddModelError(nameof(Name), outcome.Error!);
            if (Id is { } id)
            {
                await LoadAsync(id, fill: false);
            }

            return Page();
        }

        Flash(Text.T("Common.SavedNamed", outcome.Value!.Name));
        return LocalRedirect($"/rollout/images/edit?id={outcome.Value.Id}");
    }

    /// <summary>One file of the image (<paramref name="slot"/>: boot.wim, BCD, boot.sdi, kernel, initrd).</summary>
    public async Task<IActionResult> OnPostUploadAsync(string slot, IFormFile? file)
    {
        if (file is not { Length: > 0 })
        {
            FlashError(Text.T("Images.Upload.Empty"));
            return LocalRedirect($"/rollout/images/edit?id={Id}");
        }

        await using var stream = file.OpenReadStream();
        var outcome = await RolloutService.UploadImageFileAsync(db, Me.Actor, files, Id ?? 0, slot, stream, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        Flash(Text.T("Images.Uploaded", slot));
        return LocalRedirect($"/rollout/images/edit?id={Id}");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var outcome = await RolloutService.DeleteImageAsync(db, Me.Actor, files, Id ?? 0, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        Flash(Text.T("Common.DeletedNamed", outcome.Value!));
        return LocalRedirect("/rollout/images");
    }

    private async Task<bool> LoadAsync(long id, bool fill)
    {
        var image = await RolloutService.ImageAsync(db, Me.Actor, id, HttpContext.RequestAborted);
        if (image is null)
        {
            return false;
        }

        if (fill)
        {
            Name = image.Name;
            Description = image.Description ?? string.Empty;
            KernelArguments = image.KernelArguments ?? string.Empty;
        }

        Kind = image.Kind;
        Files = RolloutService.FileSizes(files, image);
        return true;
    }
}
