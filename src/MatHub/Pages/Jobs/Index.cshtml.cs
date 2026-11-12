using MatHub.Data;
using MatHub.Localization;
using MatHub.Services;
using MatHub.Services.Access;
using MatHub.Services.Jobs;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Jobs;

/// <summary>The jobs with a run on a client the person reaches: newest first, open, failed or done, scripts, packages or system.</summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    private const int PageSize = 30;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public IReadOnlyList<JobRow> Items { get; private set; } = [];

    public string Zone => Me.TimeZone;

    public bool CanRun => Me.Can(Permission.ScriptsRun) || Me.Can(Permission.PackagesDeploy);

    /// <summary>Something still runs: the page refreshes itself (meta refresh would be inline; offline.js leaves it alone).</summary>
    public bool Busy => Items.Any(j => j.Queued + j.Running > 0);

    public async Task<IActionResult> OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var state = ListQuery.Text(Request, "state");
        var kind = ListQuery.Text(Request, "kind");
        var page = ListQuery.Page(Request);
        var list = await JobService.ListAsync(db, Me.Actor, new JobCriteria(search, state, kind, null, page, PageSize), HttpContext.RequestAborted);
        if (list is null)
        {
            return NotFound();
        }

        Items = list.Items;
        Toolbar = new ToolbarModel
        {
            Id = "jobs",
            Action = "/jobs",
            Label = Text.T("Jobs.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Jobs.Search"),
            Filters =
            [
                new FilterField("state", Text.T("Common.Status"), [new("open", Text.T("Jobs.State.Open")), new("failed", Text.T("Jobs.State.Failed")), new("done", Text.T("Jobs.State.Done"))], state),
                new FilterField("kind", Text.T("Jobs.Kind"), [new("script", Text.T("Scripts.Title")), new("package", Text.T("Packages.Title")), new("system", Text.T("Jobs.Kind.System"))], kind),
            ],
            SortOptions = [new("date", Text.T("Jobs.Col.Created"))],
            Sort = "date",
            Descending = true,
            DefaultSort = "date",
            DefaultDescending = true,
            Total = list.Total,
            Matching = list.Matching,
            Noun = Text.T("Jobs.Many"),
            NounOne = Text.T("Jobs.One"),
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = list.Matching, Url = Toolbar.UrlWithPage };
        return Page();
    }
}
