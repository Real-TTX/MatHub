using System.Globalization;
using MatHub.Controls;
using MatHub.Data;
using MatHub.Localization;
using MatHub.Services.Access;
using MatHub.Services.Clients;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Admin.Roles;

/// <summary>A role: name, description, the permissions (by area) and the clients it reaches (all, or the members of some groups).</summary>
public sealed class EditModel(AppDbContext db) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    [BindProperty] public string Name { get; set; } = string.Empty;

    [BindProperty] public string Description { get; set; } = string.Empty;

    [BindProperty] public List<string> Permissions { get; set; } = [];

    [BindProperty] public string Scope { get; set; } = "all";

    [BindProperty] public string Groups { get; set; } = string.Empty;

    public string? Key { get; private set; }

    public bool IsNew => Id is null;

    public List<Choice> GroupChoices { get; private set; } = [];

    public List<Choice> Scopes { get; } = [new("all", Text.T("Roles.AllClients")), new("groups", Text.T("Roles.SomeGroups"))];

    /// <summary>The permissions by area, in the order of <see cref="Permission.All"/>.</summary>
    public IEnumerable<IGrouping<string, string>> Areas => Permission.All.GroupBy(Permission.AreaOf);

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadChoicesAsync();
        if (Id is not { } id)
        {
            return Page();
        }

        if (await RoleService.GetAsync(db, Me.Actor, id, HttpContext.RequestAborted) is not { } found)
        {
            return NotFound();
        }

        var (role, groups) = found;
        Name = role.Name;
        Description = role.Description ?? string.Empty;
        Permissions = Permission.Parse(role.Permissions).ToList();
        Scope = role.AllClients ? "all" : "groups";
        Groups = string.Join(", ", groups);
        Key = role.Key;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var ids = Groups.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) ? g : 0).Where(g => g > 0).ToList();
        var outcome = await RoleService.SaveAsync(db, Me.Actor, Id, new RoleForm(Name, Description, Permissions, Scope != "groups", ids), HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            await LoadChoicesAsync();
            ModelState.AddModelError(string.IsNullOrWhiteSpace(Name) ? nameof(Name) : nameof(Groups), outcome.Error!);
            return Page();
        }

        Flash(Text.T("Common.SavedNamed", outcome.Value!.Name));
        return LocalRedirect("/Admin/Roles");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var outcome = await RoleService.DeleteAsync(db, Me.Actor, Id ?? 0, HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        Flash(Text.T("Common.DeletedNamed", outcome.Value!));
        return LocalRedirect("/Admin/Roles");
    }

    private async Task LoadChoicesAsync()
        => GroupChoices = (await GroupService.ChoicesAsync(db, Me.Actor, HttpContext.RequestAborted))
            .Select(g => new Choice(g.Id.ToString(CultureInfo.InvariantCulture), g.Name, null, g.Colour)).ToList();
}
