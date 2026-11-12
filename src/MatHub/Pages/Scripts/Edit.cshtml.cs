using MatHub.Controls;
using MatHub.Data;
using MatHub.Localization;
using MatHub.Services;
using MatHub.Services.Access;
using MatHub.Services.Scripts;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Scripts;

/// <summary>
/// A script, tab General: name, description, category, the editor (code, blocks, flow), the language (only for code: the visual editors write PowerShell) and
/// the time it may take. A new script goes on to its editor after the first save.
/// </summary>
public sealed class EditModel(AppDbContext db) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    [BindProperty] public string Name { get; set; } = string.Empty;

    [BindProperty] public string Description { get; set; } = string.Empty;

    [BindProperty] public string Category { get; set; } = string.Empty;

    [BindProperty] public ScriptEditor Editor { get; set; } = ScriptEditor.Blocks;

    [BindProperty] public ScriptLanguage Language { get; set; } = ScriptLanguage.PowerShell;

    [BindProperty] public int TimeoutMinutes { get; set; } = 10;

    public bool IsNew => Id is null;

    public bool CanEdit => Me.Can(Permission.ScriptsEdit);

    public bool CanRun => Me.Can(Permission.ScriptsRun);

    public bool CanKiosk => Me.Can(Permission.KioskManage) && Me.Can(Permission.ScriptsRun);

    public List<Choice> Editors { get; } = Enum.GetValues<ScriptEditor>().Select(e => new Choice(e.ToString(), Text.T("Scripts.Editor." + e), Text.T("Scripts.Editor." + e + ".Hint"))).ToList();

    public List<Choice> Languages { get; } = Enum.GetValues<ScriptLanguage>().Select(l => new Choice(l.ToString(), Text.T("Scripts.Language." + l))).ToList();

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id is not { } id)
        {
            return CanEdit ? Page() : NotFound();
        }

        var script = await ScriptService.GetAsync(db, Me.Actor, id, HttpContext.RequestAborted);
        if (script is null)
        {
            return NotFound();
        }

        Name = script.Name;
        Description = script.Description ?? string.Empty;
        Category = script.Category ?? string.Empty;
        Editor = script.Editor;
        Language = script.Language;
        TimeoutMinutes = Math.Max(1, script.TimeoutSeconds / 60);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var outcome = await ScriptService.SaveAsync(db, Me.Actor, Id, new ScriptForm(Name, Description, Category, Language, Editor, TimeoutMinutes * 60), HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            ModelState.AddModelError(string.IsNullOrWhiteSpace(Name) ? nameof(Name) : nameof(TimeoutMinutes), outcome.Error!);
            return Page();
        }

        Flash(Text.T("Common.SavedNamed", outcome.Value!.Name));
        return LocalRedirect(Id is null ? $"/scripts/{outcome.Value.Id}/code" : $"/scripts/edit?id={outcome.Value.Id}");
    }

    /// <summary>The templates a new script can begin with.</summary>
    public IReadOnlyList<ScriptTemplate> Templates => ScriptTemplates.All;

    public async Task<IActionResult> OnPostTemplateAsync(string key)
    {
        var outcome = await ScriptService.CreateFromTemplateAsync(db, Me.Actor, key, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            FlashError(outcome.Error!);
            return LocalRedirect("/scripts/edit");
        }

        Flash(Text.T("Common.CreatedNamed", outcome.Value!.Name));
        return LocalRedirect($"/scripts/{outcome.Value.Id}/code");
    }

    public async Task<IActionResult> OnPostFlowAsync()
    {
        var outcome = await ScriptService.CopyAsFlowAsync(db, Me.Actor, Id ?? 0, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        Flash(Text.T("Common.CreatedNamed", outcome.Value!.Name));
        return LocalRedirect($"/scripts/{outcome.Value.Id}/code");
    }

    public async Task<IActionResult> OnPostDuplicateAsync()
    {
        var outcome = await ScriptService.DuplicateAsync(db, Me.Actor, Id ?? 0, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        Flash(Text.T("Common.CreatedNamed", outcome.Value!.Name));
        return LocalRedirect($"/scripts/edit?id={outcome.Value.Id}");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var outcome = await ScriptService.DeleteAsync(db, Me.Actor, Id ?? 0, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        Flash(Text.T("Common.DeletedNamed", outcome.Value!));
        return LocalRedirect("/scripts");
    }
}
