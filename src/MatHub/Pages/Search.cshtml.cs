using MatHub.Data;
using MatHub.Services;

namespace MatHub.Pages;

/// <summary>The results of the search of the top bar (Enter, or "All results"): clients, groups, scripts and packages the person may see.</summary>
public sealed class SearchModel(AppDbContext db) : AppPageModel
{
    public string Query { get; private set; } = string.Empty;

    public IReadOnlyList<SearchGroup> Groups { get; private set; } = [];

    public async Task OnGetAsync(string? q)
    {
        Query = q?.Trim() ?? string.Empty;
        Groups = await SearchService.SearchAsync(db, Me.Actor, Query, 50, HttpContext.RequestAborted);
    }
}
