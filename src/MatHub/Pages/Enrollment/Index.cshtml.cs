using MatHub.Localization;
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

    public ToolbarModel Toolbar { get; private set; } = null!;

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

        var search = ListQuery.Text(Request, "q");
        var state = ListQuery.Text(Request, "state");
        var sort = ListQuery.Text(Request, "sort") ?? "name";
        var desc = ListQuery.Descending(Request);
        IEnumerable<EnrollmentKeyRow> rows = keys;
        if (!string.IsNullOrWhiteSpace(search))
        {
            rows = rows.Where(k => k.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) || (k.GroupName?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false));
        }

        rows = state switch
        {
            "usable" => rows.Where(k => k.Usable),
            "unusable" => rows.Where(k => !k.Usable),
            "approval" => rows.Where(k => !k.AutoApprove),
            _ => rows,
        };
        rows = (sort, desc) switch
        {
            ("uses", false) => rows.OrderBy(k => k.UseCount),
            ("uses", true) => rows.OrderByDescending(k => k.UseCount),
            (_, true) => rows.OrderByDescending(k => k.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => rows.OrderBy(k => k.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        Keys = rows.ToList();
        Toolbar = new ToolbarModel
        {
            Id = "keys",
            Action = "/enrollment",
            Label = Text.T("Enrollment.Keys"),
            Search = search,
            SearchField = false,
            SearchPlaceholder = Text.T("Enrollment.Search"),
            Filters = [new FilterField("state", Text.T("Common.Status"), [new("usable", Text.T("Common.Active")), new("unusable", Text.T("Enrollment.NotUsable")), new("approval", Text.T("Enrollment.NeedsApproval"))], state)],
            SortOptions = [new("name", Text.T("Common.Name")), new("uses", Text.T("Enrollment.Uses"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "name",
            Total = keys.Count,
            Matching = Keys.Count,
            Noun = Text.T("Enrollment.Many"),
            NounOne = Text.T("Enrollment.One"),
        };
        Server = await instance.BaseUrlAsync(Request);
        return Page();
    }
}
