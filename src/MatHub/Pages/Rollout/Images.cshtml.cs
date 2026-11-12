using MatHub.Services;
using MatHub.Localization;
using MatHub.Data;
using MatHub.Services.Access;
using MatHub.Services.Rollout;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Rollout;

/// <summary>The boot images: Windows PE (boot.wim, BCD, boot.sdi) and Linux (kernel, initrd), with which of their files are there.</summary>
public sealed class ImagesModel(AppDbContext db, NetbootFiles files) : AppPageModel
{
    public IReadOnlyList<ImageRow> Items { get; private set; } = [];

    public ToolbarModel Toolbar { get; private set; } = null!;

    public bool CanManage => Me.Can(Permission.RolloutManage);

    public async Task<IActionResult> OnGetAsync()
    {
        var items = await RolloutService.ImagesAsync(db, Me.Actor, files, HttpContext.RequestAborted);
        if (items is null)
        {
            return NotFound();
        }

        var search = ListQuery.Text(Request, "q");
        var kind = ListQuery.Text(Request, "kind");
        var desc = ListQuery.Descending(Request);
        IEnumerable<ImageRow> rows = items;
        if (!string.IsNullOrWhiteSpace(search))
        {
            rows = rows.Where(i => i.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) || (i.Description?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false));
        }

        if (Enum.TryParse<BootImageKind>(kind, true, out var k))
        {
            rows = rows.Where(i => i.Kind == k);
        }

        Items = (desc ? rows.OrderByDescending(i => i.Name, StringComparer.CurrentCultureIgnoreCase) : rows.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)).ToList();
        Toolbar = new ToolbarModel
        {
            Id = "images",
            Action = "/rollout/images",
            Label = Text.T("Images.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Images.Search"),
            Filters = [new FilterField("kind", Text.T("Images.Kind"), Enum.GetValues<BootImageKind>().Select(x => new FilterOption(x.ToString(), Text.T("Images.Kind." + x))).ToList(), kind)],
            SortOptions = [new("name", Text.T("Common.Name"))],
            Sort = "name",
            Descending = desc,
            DefaultSort = "name",
            Total = items.Count,
            Matching = Items.Count,
            Noun = Text.T("Images.Many"),
            NounOne = Text.T("Images.One"),
        };
        return Page();
    }
}
