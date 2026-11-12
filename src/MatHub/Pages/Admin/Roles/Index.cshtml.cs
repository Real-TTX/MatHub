using MatHub.Data;
using MatHub.Services.Access;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Admin.Roles;

/// <summary>The roles: sets of permissions for all clients or for groups. The built-in ones can be changed, not deleted (MatIdentity names them).</summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    public IReadOnlyList<RoleRow> Items { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        var items = await RoleService.ListAsync(db, Me.Actor, HttpContext.RequestAborted);
        if (items is null)
        {
            return NotFound();
        }

        Items = items;
        return Page();
    }
}
