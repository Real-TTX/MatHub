using System.Text.Json;
using MatHub.Data;
using MatHub.Localization;
using MatHub.Services.Access;
using MatHub.Services.Scripts;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Scripts;

/// <summary>
/// A script, tab Script: the editor of its kind. The code editor (highlighting, line numbers, tab), the block editor (steps one after the other, each with an
/// optional condition) or the flow editor (steps and branches on a canvas). The visual editors post their model; MatHub compiles it to PowerShell on save
/// (and shows the result beside the editor while a person works, <c>/live/scripts/compile</c>).
/// </summary>
public sealed class CodeModel(AppDbContext db) : AppPageModel
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [BindProperty(SupportsGet = true)] public long Id { get; set; }

    [BindProperty] public string Code { get; set; } = string.Empty;

    [BindProperty] public string ModelJson { get; set; } = string.Empty;

    public Script Script { get; private set; } = null!;

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public bool CanEdit => Me.Can(Permission.ScriptsEdit);

    public bool CanRun => Me.Can(Permission.ScriptsRun);

    /// <summary>The kinds of blocks with their texts in the language of the page, for the visual editors (one source: <see cref="ScriptBlocks.Types"/>).</summary>
    public string CatalogJson => JsonSerializer.Serialize(ScriptBlocks.Types.Select(t => new
    {
        t.Id,
        t.Category,
        t.Icon,
        t.IsCondition,
        Label = Text.T("Blocks.Type." + t.Id),
        Hint = Text.T("Blocks.Hint." + t.Id),
        Fields = t.Fields.Select(f => new
        {
            f.Name,
            f.Kind,
            f.Required,
            f.Placeholder,
            Label = Text.T("Blocks.Field." + f.Name),
            Options = f.Options?.Select(o => new { Value = o, Label = Text.T("Blocks.Option." + o) }),
        }),
    }), Json);

    public string CategoriesJson => JsonSerializer.Serialize(
        new[] { "software", "files", "system", "script", "check" }.ToDictionary(c => c, c => Text.T("Blocks.Category." + c)), Json);

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        Code = Script.Content;
        ModelJson = Script.Model ?? (Script.Editor == ScriptEditor.Blocks ? "{\"blocks\":[]}" : "{}");
        if (Script.Editor != ScriptEditor.Code && Script.Model is not null)
        {
            Errors = ScriptService.Compile(Script.Editor, ScriptBlocks.Parse(Script.Model)).Errors;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var outcome = await ScriptService.SaveContentAsync(db, Me.Actor, Id, Code, ModelJson, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        var (script, errors) = outcome.Value;
        if (errors.Count > 0)
        {
            FlashError(Text.T("Scripts.SavedWithErrors", errors.Count));
        }
        else
        {
            Flash(Text.T("Common.SavedNamed", script.Name));
        }

        return LocalRedirect($"/scripts/{Id}/code");
    }

    private async Task<bool> LoadAsync()
    {
        if (await ScriptService.GetAsync(db, Me.Actor, Id, HttpContext.RequestAborted) is not { } script)
        {
            return false;
        }

        Script = script;
        return true;
    }
}
