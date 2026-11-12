using MatHub.Data;
using MatHub.Services;
using MatHub.Services.Access;
using MatHub.Services.Rollout;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Rollout;

/// <summary>How the network boot is set up: what the DHCP server has to say, which boot loaders MatHub has, which address the computers load from, the first script.</summary>
public sealed class SetupModel(AppDbContext db, NetbootFiles files, TftpServerInfo tftp, IConfiguration configuration) : AppPageModel
{
    public IReadOnlyList<(string File, string For, bool There)> Loaders { get; private set; } = [];

    public bool Wimboot { get; private set; }

    public string EntryScript { get; private set; } = string.Empty;

    public string? BootUrl { get; private set; }

    public int HttpPort { get; private set; }

    public TftpServerInfo Tftp => tftp;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Me.Can(Permission.RolloutView))
        {
            return NotFound();
        }

        Loaders = NetbootFiles.Loaders.Select(l => (l.File, l.For, files.Loader(l.File) is not null)).ToList();
        Wimboot = files.Loader("wimboot") is not null;
        EntryScript = await BootEndpoints.EntryScriptAsync(db, configuration, HttpContext.RequestAborted);
        BootUrl = InstanceService.NormalizePublicUrl((await SettingsStore.LoadAsync(db)).BootUrl);
        HttpPort = BootEndpoints.HttpPort(configuration);
        return Page();
    }
}
