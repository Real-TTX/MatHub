using MatHub.Data;
using MatHub.Services.Access;
using MatHub.Services.Clients;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Clients;

/// <summary>What every tab of a client needs: the client (only one the person reaches: else 404) and what the person may do with it.</summary>
public abstract class ClientPageModel(AppDbContext db) : AppPageModel
{
    protected AppDbContext Db => db;

    [BindProperty(SupportsGet = true)] public long Id { get; set; }

    public ClientDetail Detail { get; private set; } = null!;

    public Client Client => Detail.Client;

    public string Zone => Me.TimeZone;

    public bool CanManage => Me.Can(Permission.ClientsManage);

    public bool CanRun => Client.Status == ClientStatus.Active && (Me.Can(Permission.ScriptsRun) || Me.Can(Permission.PackagesDeploy));

    public bool CanSeeJobs => Me.Can(Permission.JobsView);

    protected async Task<bool> LoadClientAsync()
    {
        if (await ClientService.GetAsync(db, Me.Actor, Id, HttpContext.RequestAborted) is not { } detail)
        {
            return false;
        }

        Detail = detail;
        return true;
    }
}
