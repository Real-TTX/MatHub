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

    /// <summary>The conditions of a dynamic group, as the rows of the form send them.</summary>
    [BindProperty] public List<ConditionInput> Conditions { get; set; } = [new()];

    /// <summary>"all" or "any".</summary>
    [BindProperty] public string Match { get; set; } = "all";

    public List<Choice> Matches { get; } = [new("all", Text.T("Groups.Match.All"), null), new("any", Text.T("Groups.Match.Any"), null)];

    public sealed class ConditionInput
    {
        public GroupRuleField Field { get; set; } = GroupRuleField.Name;

        public GroupRuleOperator Operator { get; set; } = GroupRuleOperator.StartsWith;

        public string Value { get; set; } = string.Empty;
    }

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
        var conditions = GroupService.Conditions(group);
        Conditions = conditions.Count == 0 ? [new()] : conditions.Select(c => new ConditionInput { Field = c.Field, Operator = c.Operator, Value = c.Value }).ToList();
        Match = group.MatchAny ? "any" : "all";
        Members = await GroupService.MembersAsync(db, Me.Actor, id, HttpContext.RequestAborted);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var outcome = await GroupService.SaveAsync(db, Me.Actor, Id, new GroupForm(Name, Description, Colour, Kind, Conditions.Select(c => new GroupCondition(c.Field, c.Operator, c.Value)).ToList(), Match == "any"), HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            if (Conditions.Count == 0)
            {
                Conditions = [new()];
            }

            ModelState.AddModelError(Kind == GroupKind.Dynamic && Conditions.All(c => string.IsNullOrWhiteSpace(c.Value)) ? "Conditions" : nameof(Name), outcome.Error!);
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
