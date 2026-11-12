using MatHub.Data;
using MatHub.Localization;
using MatHub.Services.Access;
using MatHub.Services.Scripts;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Scripts;

/// <summary>A script, tab Files: what goes along with every run (an XML, a configuration, a small installer) – <c>./name</c> in the fields of the actions.</summary>
[DisableRequestSizeLimit]
public sealed class FilesModel(AppDbContext db, ScriptFileStore store) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }

    [BindProperty] public List<IFormFile> Upload { get; set; } = [];

    public Script Script { get; private set; } = null!;

    public IReadOnlyList<ScriptFile> Files { get; private set; } = [];

    public bool CanEdit => Me.Can(Permission.ScriptsEdit);

    public string Zone => Me.TimeZone;

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : NotFound();

    public async Task<IActionResult> OnPostUploadAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        var names = new List<string>();
        foreach (var file in Upload)
        {
            await using var stream = file.OpenReadStream();
            var outcome = await ScriptFileService.UploadAsync(db, Me.Actor, store, Id, file.FileName, stream, HttpContext.RequestAborted);
            if (outcome.NotAllowed)
            {
                return NotFound();
            }

            if (!outcome.Ok)
            {
                FlashError(outcome.Error!);
                return LocalRedirect($"/scripts/{Id}/files");
            }

            names.Add(outcome.Value!.Name);
        }

        if (names.Count > 0)
        {
            Flash(Text.T("ScriptFiles.Uploaded", string.Join(", ", names)));
        }

        return LocalRedirect($"/scripts/{Id}/files");
    }

    public async Task<IActionResult> OnPostDeleteAsync(long file)
    {
        var outcome = await ScriptFileService.DeleteAsync(db, Me.Actor, store, Id, file, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        Flash(Text.T("Common.DeletedNamed", outcome.Value!));
        return LocalRedirect($"/scripts/{Id}/files");
    }

    private async Task<bool> LoadAsync()
    {
        if (await ScriptService.GetAsync(db, Me.Actor, Id, HttpContext.RequestAborted) is not { } script)
        {
            return false;
        }

        Script = script;
        Files = await ScriptFileService.ListAsync(db, Me.Actor, Id, HttpContext.RequestAborted);
        return true;
    }
}
