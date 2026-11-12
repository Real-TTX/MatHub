using MatHub.Controls;
using MatHub.Data;
using MatHub.Localization;
using MatHub.Services;
using MatHub.Services.Access;
using MatHub.Services.Clients;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Groups;

/// <summary>A group: name, description, colour, by hand or by a rule (the rule fields only for a dynamic group), and its members.</summary>
public sealed class EditModel(AppDbContext db) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    [BindProperty] public string Name { get; set; } = string.Empty;

    [BindProperty] public string Description { get; set; } = string.Empty;

    [BindProperty] public string Colour { get; set; } = string.Empty;

    [BindProperty] public GroupKind Kind { get; set; } = GroupKind.Static;

    [BindProperty] public GroupRuleField RuleField { get; set; } = GroupRuleField.Name;

    [BindProperty] public GroupRuleOperator RuleOperator { get; set; } = GroupRuleOperator.StartsWith;

    [BindProperty] public string RuleValue { get; set; } = string.Empty;

    public IReadOnlyList<ClientRow> Members { get; private set; } = [];

    public bool IsNew => Id is null;

    public bool CanManage => Me.Can(Permission.GroupsManage) && (Id is not null || Me.Actor.ReachesAll);

    public string Zone => Me.TimeZone;

    public List<Choice> Kinds { get; } = [new("Static", Text.T("Groups.Kind.Static"), Text.T("Groups.Kind.Static.Hint")), new("Dynamic", Text.T("Groups.Kind.Dynamic"), Text.T("Groups.Kind.Dynamic.Hint"))];

    public List<Choice> Fields { get; } = Enum.GetValues<GroupRuleField>().Select(f => new Choice(f.ToString(), Text.T("Groups.Field." + f))).ToList();

    public List<Choice> Operators { get; } = Enum.GetValues<GroupRuleOperator>().Select(o => new Choice(o.ToString(), Text.T("Groups.Operator." + o))).ToList();

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id is not { } id)
        {
            return CanManage ? Page() : NotFound();
        }

        var group = await GroupService.GetAsync(db, Me.Actor, id, HttpContext.RequestAborted);
        if (group is null)
        {
            return NotFound();
        }

        Name = group.Name;
        Description = group.Description ?? string.Empty;
        Colour = group.Colour ?? string.Empty;
        Kind = group.Kind;
        RuleField = group.RuleField;
        RuleOperator = group.RuleOperator;
        RuleValue = group.RuleValue ?? string.Empty;
        Members = await GroupService.MembersAsync(db, Me.Actor, id, HttpContext.RequestAborted);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var outcome = await GroupService.SaveAsync(db, Me.Actor, Id, new GroupForm(Name, Description, Colour, Kind, RuleField, RuleOperator, RuleValue), HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            ModelState.AddModelError(Kind == GroupKind.Dynamic && string.IsNullOrWhiteSpace(RuleValue) ? nameof(RuleValue) : nameof(Name), outcome.Error!);
            if (Id is { } id)
            {
                Members = await GroupService.MembersAsync(db, Me.Actor, id, HttpContext.RequestAborted);
            }

            return Page();
        }

        Flash(Text.T("Common.SavedNamed", outcome.Value!.Name));
        return LocalRedirect($"/groups/edit?id={outcome.Value.Id}");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var outcome = await GroupService.DeleteAsync(db, Me.Actor, Id ?? 0, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        Flash(Text.T("Common.DeletedNamed", outcome.Value!));
        return LocalRedirect("/groups");
    }
}
