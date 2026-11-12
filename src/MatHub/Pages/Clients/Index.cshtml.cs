using MatHub.Services.Packages;
using MatHub.Data;
using MatHub.Localization;
using MatHub.Services;
using MatHub.Services.Access;
using MatHub.Services.Clients;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Clients;

/// <summary>The clients the person reaches: search, state, group, operating system; several at once can be given a job.</summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    private const int PageSize = 50;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public IReadOnlyList<ClientRow> Items { get; private set; } = [];

    public string Zone => Me.TimeZone;

    public bool CanRun => Me.Can(Permission.ScriptsRun) || Me.Can(Permission.PackagesDeploy);

    public bool CanEnroll => Me.Can(Permission.ClientsEnroll);

    public async Task<IActionResult> OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var search = ListQuery.Text(Request, "q");
        var state = ListQuery.Text(Request, "state");
        var group = long.TryParse(ListQuery.Text(Request, "group"), out var g) ? g : (long?)null;
        var system = ListQuery.Text(Request, "os");
        var update = ListQuery.Text(Request, "update");
        var sort = ListQuery.Text(Request, "sort") ?? "name";
        var desc = ListQuery.Descending(Request);
        var page = ListQuery.Page(Request);
        var list = await ClientService.ListAsync(db, Me.Actor, new ClientCriteria(search, state, group, system, sort, desc, page, PageSize, update), ct);
        if (list is null)
        {
            return NotFound();
        }

        Items = list.Items;
        var groups = await GroupService.ChoicesAsync(db, Me.Actor, ct);
        var systems = await ClientService.SystemsAsync(db, Me.Actor, ct);
        var updates = await UpdateService.ListAsync(db, Me.Actor, null, null, ct);
        Toolbar = new ToolbarModel
        {
            Id = "clients",
            Action = "/clients",
            Label = Text.T("Clients.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Clients.Search"),
            Filters =
            [
                new FilterField("state", Text.T("Common.Status"), new[] { "online", "offline", "pending", "disabled", "updates" }.Select(s => new FilterOption(s, Text.T("Clients.State." + Capital(s)))).ToList(), state),
                new FilterField("group", Text.T("Groups.One"), groups.Select(x => new FilterOption(x.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), x.Name)).ToList(), group?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new FilterField("os", Text.T("Clients.Col.System"), systems.Select(s => new FilterOption(s, Ui.Os(s))).ToList(), system),
                new FilterField("update", Text.T("Clients.UpdateFor"), (updates?.Items ?? []).Select(u => new FilterOption(u.WingetId, u.Name)).ToList(), update),
            ],
            SortOptions = [new("name", Text.T("Common.Name")), new("seen", Text.T("Clients.Col.LastSeen")), new("os", Text.T("Clients.Col.System")), new("updates", Text.T("Clients.Col.Updates")), new("created", Text.T("Clients.Enrolled"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "name",
            Total = list.Total,
            Matching = list.Matching,
            Noun = Text.T("Clients.Many"),
            NounOne = Text.T("Clients.One"),
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = list.Matching, Url = Toolbar.UrlWithPage };
        return Page();
    }

    private static string Capital(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
