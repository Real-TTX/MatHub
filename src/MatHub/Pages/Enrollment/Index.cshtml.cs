using MatHub.Data;
using MatHub.Services;
using MatHub.Services.Agent;
using MatHub.Services.Clients;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Enrollment;

/// <summary>
/// How computers come into MatHub: the enrollment keys (list) and how the agent is installed (the line for an elevated PowerShell, the download). A key made
/// a moment ago is shown here once (TempData), with its ready line.
/// </summary>
public sealed class IndexModel(AppDbContext db, AgentFiles agent, InstanceService instance) : AppPageModel
{
    public IReadOnlyList<EnrollmentKeyRow> Keys { get; private set; } = [];

    public string Server { get; private set; } = string.Empty;

    public AgentFiles Agent => agent;

    /// <summary>The key made a moment ago (shown once).</summary>
    public string? NewKey => TempData["NewKey"] as string;

    public string Zone => Me.TimeZone;

    public async Task<IActionResult> OnGetAsync()
    {
        var keys = await EnrollmentService.ListAsync(db, Me.Actor, HttpContext.RequestAborted);
        if (keys is null)
        {
            return NotFound();
        }

        Keys = keys;
        Server = await instance.BaseUrlAsync(Request);
        return Page();
    }
}
