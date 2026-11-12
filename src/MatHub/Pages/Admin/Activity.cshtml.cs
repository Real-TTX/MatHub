using MatHub.Data;
using MatHub.Localization;
using MatHub.Services;

namespace MatHub.Pages.Admin;

/// <summary>The activity of the whole installation for the administrators: who changed, ran or enrolled what, and when.</summary>
public sealed class ActivityModel(AppDbContext db) : AppPageModel
{
    private const int PageSize = 50;

    public IReadOnlyList<ActivityRow> Items { get; private set; } = [];

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public string Zone => Me.TimeZone;

    public async Task OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var area = ListQuery.Text(Request, "area") is { } a && ActivityLog.Areas.Contains(a) ? a : null;
        var page = ListQuery.Page(Request);
        var result = await ActivityLog.AllAsync(db, Me.Actor, search, area, page, PageSize, HttpContext.RequestAborted) ?? ([], 0);
        Items = result.Rows;
        Toolbar = new ToolbarModel
        {
            Id = "activity",
            Action = "/Admin/Activity",
            Label = Text.T("Activity.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Activity.Search"),
            Filters = [new FilterField("area", Text.T("Activity.Col.Area"), ActivityLog.Areas.Select(x => new FilterOption(x, Text.T("Activity.Area." + x))).ToList(), area)],
            SortOptions = [new("date", Text.T("Activity.Col.Date"))],
            Sort = "date",
            Descending = true,
            DefaultSort = "date",
            DefaultDescending = true,
            Total = result.Total,
            Matching = result.Total,
            Noun = Text.T("Activity.Many"),
            NounOne = Text.T("Activity.One"),
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = result.Total, Url = Toolbar.UrlWithPage };
    }
}
