using System.Globalization;
using MatHub.Controls;
using MatHub.Data;
using MatHub.Localization;
using MatHub.Services.Access;
using MatHub.Services.Jobs;
using MatHub.Services.Packages;
using MatHub.Services.Scripts;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Sequences;

/// <summary>
/// A sequence: name, category, description and its steps (scripts, packages, restarts, waits, an inventory, updates), in their order. The steps are rows of a
/// list (sequence.js adds, moves and removes them); the business layer checks what they use.
/// </summary>
public sealed class EditModel(AppDbContext db) : AppPageModel
{
    public sealed class StepInput
    {
        public SequenceStepKind Kind { get; set; } = SequenceStepKind.Script;

        public string? ScriptId { get; set; }

        public string? PackageId { get; set; }

        public string? WingetId { get; set; }

        public int? Seconds { get; set; }

        public bool ContinueOnError { get; set; }
    }

    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    [BindProperty] public string Name { get; set; } = string.Empty;

    [BindProperty] public string Category { get; set; } = string.Empty;

    [BindProperty] public string Description { get; set; } = string.Empty;

    [BindProperty] public List<StepInput> Steps { get; set; } = [];

    public List<Choice> ScriptChoices { get; private set; } = [];

    public List<Choice> PackageChoices { get; private set; } = [];

    public int Runs { get; private set; }

    public bool IsNew => Id is null;

    public bool CanEdit => Me.Can(Permission.ScriptsEdit);

    public bool CanRun => Me.Can(Permission.ScriptsRun);

    public bool CanKiosk => Me.Can(Permission.KioskManage);

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id is not { } id)
        {
            if (!CanEdit)
            {
                return NotFound();
            }

            Steps = [new StepInput { Kind = SequenceStepKind.Script }, new StepInput { Kind = SequenceStepKind.Restart }, new StepInput { Kind = SequenceStepKind.Inventory }];
            await LoadAsync();
            return Page();
        }

        var sequence = await SequenceService.GetAsync(db, Me.Actor, id, HttpContext.RequestAborted);
        if (sequence is null)
        {
            return NotFound();
        }

        Name = sequence.Name;
        Category = sequence.Category ?? string.Empty;
        Description = sequence.Description ?? string.Empty;
        Steps = SequenceService.StepsOf(sequence).Select(s => new StepInput
        {
            Kind = s.Kind,
            ScriptId = s.ScriptId?.ToString(CultureInfo.InvariantCulture),
            PackageId = s.PackageId?.ToString(CultureInfo.InvariantCulture),
            WingetId = s.WingetId,
            Seconds = s.Seconds,
            ContinueOnError = s.ContinueOnError,
        }).ToList();
        Runs = (await SequenceService.ListAsync(db, Me.Actor, null, null, "name", false, HttpContext.RequestAborted))?.FirstOrDefault(r => r.Id == id)?.Runs ?? 0;
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var steps = Steps.Select(s => new SequenceStep
        {
            Kind = s.Kind,
            ScriptId = long.TryParse(s.ScriptId, CultureInfo.InvariantCulture, out var sid) ? sid : null,
            PackageId = long.TryParse(s.PackageId, CultureInfo.InvariantCulture, out var pid) ? pid : null,
            WingetId = s.WingetId,
            Seconds = s.Seconds,
            ContinueOnError = s.ContinueOnError,
        }).ToList();
        var outcome = await SequenceService.SaveAsync(db, Me.Actor, Id, new SequenceForm(Name, Description, Category, steps), HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            ModelState.AddModelError(string.IsNullOrWhiteSpace(Name) ? nameof(Name) : nameof(Steps), outcome.Error!);
            await LoadAsync();
            return Page();
        }

        Flash(Text.T("Common.SavedNamed", outcome.Value!.Name));
        return LocalRedirect($"/sequences/edit?id={outcome.Value.Id}");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var outcome = await SequenceService.DeleteAsync(db, Me.Actor, Id ?? 0, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        Flash(Text.T("Common.DeletedNamed", outcome.Value!));
        return LocalRedirect("/sequences");
    }

    private async Task LoadAsync()
    {
        var ct = HttpContext.RequestAborted;
        ScriptChoices = (await ScriptService.ChoicesAsync(db, Me.Actor, ct)).Select(x => new Choice(x.Id.ToString(CultureInfo.InvariantCulture), x.Name)).ToList();
        PackageChoices = (await PackageService.ChoicesAsync(db, Me.Actor, ct)).Select(x => new Choice(x.Id.ToString(CultureInfo.InvariantCulture), x.Name)).ToList();
    }
}
