using MatHub.Data;
using MatHub.Localization;
using MatHub.Services;
using MatHub.Services.Access;
using MatHub.Services.Clients;
using MatHub.Services.Packages;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Clients;

/// <summary>A client, tab Software: the installed programs of the last inventory, with the updates winget offers and a button to install them.</summary>
public sealed class SoftwareModel(AppDbContext db) : ClientPageModel(db)
{
    public IReadOnlyList<SoftwareRow> Items { get; private set; } = [];

    public ToolbarModel Toolbar { get; private set; } = null!;

    public int UpdateCount { get; private set; }

    public bool CanUpdate => Client.Status == ClientStatus.Active && Me.Can(Permission.PackagesDeploy);

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadClientAsync())
        {
            return NotFound();
        }

        var search = ListQuery.Text(Request, "q");
        var onlyUpdates = ListQuery.Text(Request, "updates") == "1";
        var all = await ClientService.SoftwareAsync(Db, Me.Actor, Id, null, false, HttpContext.RequestAborted) ?? [];
        Items = await ClientService.SoftwareAsync(Db, Me.Actor, Id, search, onlyUpdates, HttpContext.RequestAborted) ?? [];
        UpdateCount = all.Count(s => s.AvailableVersion is not null);
        Toolbar = new ToolbarModel
        {
            Id = "software",
            Action = $"/clients/{Id}/software",
            Label = Text.T("Clients.Tab.Software"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Software.Search"),
            Filters = [new FilterField("updates", Text.T("Software.Show"), [new("1", Text.T("Software.OnlyUpdates"))], onlyUpdates ? "1" : null)],
            SortOptions = [new("name", Text.T("Common.Name"))],
            Sort = "name",
            DefaultSort = "name",
            Total = all.Count,
            Matching = Items.Count,
            Noun = Text.T("Software.Many"),
            NounOne = Text.T("Software.One"),
        };
        return Page();
    }

    /// <summary>Installs one update (<paramref name="wingetId"/>) or all of them on this client.</summary>
    public async Task<IActionResult> OnPostUpdateAsync(string? wingetId)
    {
        var outcome = await UpdateService.UpdateAsync(Db, Me.Actor, string.IsNullOrWhiteSpace(wingetId) ? null : wingetId, null, [Id], HttpContext.RequestAborted);
        if (outcome.NotAllowed)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            FlashError(outcome.Error!);
            return LocalRedirect($"/clients/{Id}/software");
        }

        Flash(Text.T("Jobs.Started", outcome.Value!.Name));
        return LocalRedirect($"/jobs/{outcome.Value.Id}");
    }
}
