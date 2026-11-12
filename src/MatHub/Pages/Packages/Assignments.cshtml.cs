using System.Globalization;
using MatHub.Controls;
using MatHub.Data;
using MatHub.Localization;
using MatHub.Services.Access;
using MatHub.Services.Clients;
using MatHub.Services.Packages;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Packages;

/// <summary>
/// A package, tab Assignments: which groups have it (or must not have it). MatHub makes the jobs by itself (the planner, every minute) and shows how many
/// members have it already.
/// </summary>
public sealed class AssignmentsModel(AppDbContext db) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }

    [BindProperty] public string GroupId { get; set; } = string.Empty;

    [BindProperty] public AssignmentAction Action { get; set; } = AssignmentAction.Install;

    public Package Package { get; private set; } = null!;

    public IReadOnlyList<AssignmentRow> Items { get; private set; } = [];

    public List<Choice> Groups { get; private set; } = [];

    public List<Choice> Actions { get; } = [new("Install", Text.T("Assignments.Install")), new("Uninstall", Text.T("Assignments.Uninstall"))];

    public bool CanDeploy => Me.Can(Permission.PackagesDeploy);

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : NotFound();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!long.TryParse(GroupId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var group))
        {
            if (!await LoadAsync())
            {
                return NotFound();
            }

            ModelState.AddModelError(nameof(GroupId), Text.T("Assignments.Group.Required"));
            return Page();
        }

        var outcome = await PackageService.AssignAsync(db, Me.Actor, Id, group, Action, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            FlashError(outcome.Error!);
        }
        else
        {
            Flash(Text.T("Assignments.Saved"));
        }

        return LocalRedirect($"/packages/{Id}/assignments");
    }

    public async Task<IActionResult> OnPostRemoveAsync(long assignment)
    {
        if (!await PackageService.UnassignAsync(db, Me.Actor, assignment, HttpContext.RequestAborted))
        {
            return NotFound();
        }

        Flash(Text.T("Assignments.Removed"));
        return LocalRedirect($"/packages/{Id}/assignments");
    }

    private async Task<bool> LoadAsync()
    {
        var ct = HttpContext.RequestAborted;
        if (await PackageService.GetAsync(db, Me.Actor, Id, ct) is not { } package)
        {
            return false;
        }

        Package = package;
        Items = await PackageService.AssignmentsAsync(db, Me.Actor, Id, ct);
        var taken = Items.Select(i => i.GroupId).ToHashSet();
        Groups = (await GroupService.ChoicesAsync(db, Me.Actor, ct)).Where(g => !taken.Contains(g.Id))
            .Select(g => new Choice(g.Id.ToString(CultureInfo.InvariantCulture), g.Name, null, g.Colour)).ToList();
        return true;
    }
}
