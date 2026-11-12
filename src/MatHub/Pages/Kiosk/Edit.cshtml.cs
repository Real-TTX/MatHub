using System.Globalization;
using MatHub.Controls;
using MatHub.Data;
using MatHub.Localization;
using MatHub.Services.Access;
using MatHub.Services.Clients;
using MatHub.Services.Jobs;
using MatHub.Services.Kiosk;
using MatHub.Services.Packages;
using MatHub.Services.Scripts;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Kiosk;

/// <summary>
/// An offer of the kiosk: what the people see (name, text, icon, category), what it starts (a script, the install of a package, a sequence), for whom (every
/// client or the clients of groups), a question before it starts, and whether it is offered now. Other pages lead here with a choice made: <c>?script=</c>,
/// <c>?package=</c>, <c>?sequence=</c>.
/// </summary>
public sealed class EditModel(AppDbContext db) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    [BindProperty] public string Name { get; set; } = string.Empty;

    [BindProperty] public string Description { get; set; } = string.Empty;

    [BindProperty] public string Category { get; set; } = string.Empty;

    [BindProperty] public string Icon { get; set; } = "box";

    [BindProperty] public KioskKind Kind { get; set; } = KioskKind.Script;

    [BindProperty] public string ScriptId { get; set; } = string.Empty;

    [BindProperty] public string PackageId { get; set; } = string.Empty;

    [BindProperty] public string SequenceId { get; set; } = string.Empty;

    [BindProperty] public string Groups { get; set; } = string.Empty;

    [BindProperty] public string Confirm { get; set; } = string.Empty;

    [BindProperty] public bool IsActive { get; set; } = true;

    [BindProperty] public int SortOrder { get; set; } = 100;

    public List<Choice> Kinds { get; } = Enum.GetValues<KioskKind>().Select(k => new Choice(k.ToString(), Text.T("Kiosk.Kind." + k), Text.T("Kiosk.Kind." + k + ".Hint"))).ToList();

    public List<Choice> ScriptChoices { get; private set; } = [];

    public List<Choice> PackageChoices { get; private set; } = [];

    public List<Choice> SequenceChoices { get; private set; } = [];

    public List<Choice> GroupChoices { get; private set; } = [];

    public bool IsNew => Id is null;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Me.Can(Permission.KioskManage))
        {
            return NotFound();
        }

        var ct = HttpContext.RequestAborted;
        if (Id is { } id)
        {
            var item = await KioskService.GetAsync(db, Me.Actor, id, ct);
            if (item is null)
            {
                return NotFound();
            }

            Name = item.Name;
            Description = item.Description ?? string.Empty;
            Category = item.Category ?? string.Empty;
            Icon = item.Icon;
            Kind = item.Kind;
            ScriptId = item.ScriptId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            PackageId = item.PackageId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            SequenceId = item.SequenceId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            Groups = string.Join(", ", item.GroupIds);
            Confirm = item.Confirm ?? string.Empty;
            IsActive = item.IsActive;
            SortOrder = item.SortOrder;
        }
        else
        {
            // led here from a script, a package or a sequence: the offer is that one, with its name
            var q = Request.Query;
            if (long.TryParse(q["script"], out var script) && await ScriptService.GetAsync(db, Me.Actor, script, ct) is { } s)
            {
                (Kind, ScriptId, Name, Description, Icon) = (KioskKind.Script, s.Id.ToString(CultureInfo.InvariantCulture), s.Name, s.Description ?? string.Empty, "wrench");
            }
            else if (long.TryParse(q["package"], out var package) && await PackageService.GetAsync(db, Me.Actor, package, ct) is { } p)
            {
                (Kind, PackageId, Name, Description, Icon) = (KioskKind.Package, p.Id.ToString(CultureInfo.InvariantCulture), p.Name, p.Description ?? string.Empty, "download");
            }
            else if (long.TryParse(q["sequence"], out var sequence) && await SequenceService.GetAsync(db, Me.Actor, sequence, ct) is { } x)
            {
                (Kind, SequenceId, Name, Description, Icon) = (KioskKind.Sequence, x.Id.ToString(CultureInfo.InvariantCulture), x.Name, x.Description ?? string.Empty, "rocket");
            }
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var groups = Groups.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) ? g : 0).Where(g => g > 0).ToList();
        var form = new KioskForm(Name, Description, Category, Icon, Kind, ParseId(ScriptId), ParseId(PackageId), ParseId(SequenceId), groups, Confirm, IsActive, SortOrder);
        var outcome = await KioskService.SaveAsync(db, Me.Actor, Id, form, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            var field = string.IsNullOrWhiteSpace(Name) ? nameof(Name) : Kind switch { KioskKind.Package => nameof(PackageId), KioskKind.Sequence => nameof(SequenceId), _ => nameof(ScriptId) };
            ModelState.AddModelError(field, outcome.Error!);
            await LoadAsync();
            return Page();
        }

        Flash(Text.T("Common.SavedNamed", outcome.Value!.Name));
        return LocalRedirect("/kiosk");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var outcome = await KioskService.DeleteAsync(db, Me.Actor, Id ?? 0, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        Flash(Text.T("Common.DeletedNamed", outcome.Value!));
        return LocalRedirect("/kiosk");
    }

    private static long? ParseId(string value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;

    private async Task LoadAsync()
    {
        var actor = Me.Actor;
        var ct = HttpContext.RequestAborted;
        ScriptChoices = (await ScriptService.ChoicesAsync(db, actor, ct)).Select(x => new Choice(x.Id.ToString(CultureInfo.InvariantCulture), x.Name)).ToList();
        PackageChoices = (await PackageService.ChoicesAsync(db, actor, ct)).Select(x => new Choice(x.Id.ToString(CultureInfo.InvariantCulture), x.Name, Text.T("Packages.Kind." + x.Kind))).ToList();
        SequenceChoices = (await SequenceService.ChoicesAsync(db, actor, ct)).Select(x => new Choice(x.Id.ToString(CultureInfo.InvariantCulture), x.Name)).ToList();
        GroupChoices = (await GroupService.ChoicesAsync(db, actor, ct)).Select(x => new Choice(x.Id.ToString(CultureInfo.InvariantCulture), x.Name, null, x.Colour)).ToList();
    }
}
