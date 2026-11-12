using MatHub.Data;
using MatHub.Services.Access;
using MatHub.Services.Clients;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services;

/// <summary>What the menu shows besides its items: the counts of its badges (only what the person may see).</summary>
public sealed class MenuModel
{
    public int Pending { get; init; }

    public int Offline { get; init; }

    public int ClientsWithUpdates { get; init; }

    public int OpenRuns { get; init; }

    public int RolloutsOpen { get; init; }
}

/// <summary>One menu of the switcher at the foot of the menu.</summary>
public sealed record MenuArea(string Id, string Title, string Icon);

public sealed class MenuService(AppDbContext db, CurrentUser me)
{
    private MenuModel? _model;

    /// <summary>The menus of the switcher: MatHub, the settings of the person and, for an administrator, the administration.</summary>
    public static IReadOnlyList<MenuArea> Areas(bool admin)
    {
        var list = new List<MenuArea>
        {
            new("hub", Localization.Text.T("Shell.Area.Hub"), "monitor"),
            new("settings", Localization.Text.T("Settings.Title"), "settings"),
        };
        if (admin)
        {
            list.Add(new MenuArea("admin", Localization.Text.T("Admin.Title"), "shield"));
        }

        return list;
    }

    /// <summary>The menu a page belongs to, by its address.</summary>
    public static string AreaOf(string? path)
    {
        path ??= "/";
        bool Under(string prefix) => path.Equals(prefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
        if (Under("/Admin"))
        {
            return "admin";
        }

        return Under("/Account") ? "settings" : "hub";
    }

    public async Task<MenuModel> GetAsync()
    {
        if (_model is not null)
        {
            return _model;
        }

        var actor = me.Actor;
        var clients = AccessRules.Clients(db, actor);
        var since = DateTime.UtcNow - ClientService.OnlineWindow;
        var canClients = actor.Can(Permission.ClientsView);
        _model = new MenuModel
        {
            Pending = actor.Can(Permission.ClientsEnroll) ? await clients.CountAsync(c => c.Status == ClientStatus.Pending) : 0,
            ClientsWithUpdates = await Packages.UpdateService.PendingCountAsync(db, actor),
            OpenRuns = actor.Can(Permission.JobsView)
                ? await db.JobRuns.CountAsync(r => (r.Status == RunStatus.Queued || r.Status == RunStatus.Running) && clients.Any(c => c.Id == r.ClientId))
                : 0,
            RolloutsOpen = actor.Can(Permission.RolloutView)
                ? await db.RolloutTasks.CountAsync(t => t.Status == RolloutStatus.Waiting || t.Status == RolloutStatus.Installing)
                : 0,
        };
        return _model;
    }
}
