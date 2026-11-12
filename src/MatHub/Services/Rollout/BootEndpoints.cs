using MatHub.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services.Rollout;

/// <summary>
/// The addresses of the network boot (<c>/boot/…</c>, anonymous: iPXE and Windows PE have no account). What they hand out: the first script, the decision for a
/// MAC address, wimboot, the files of the boot images and the files of a running task (behind its random token, only while it runs).
/// </summary>
public static class BootEndpoints
{
    public static void MapBootEndpoints(this WebApplication app)
    {
        var boot = app.MapGroup("/boot").AllowAnonymous();

        boot.MapGet("/mathub.ipxe", async (AppDbContext db, IConfiguration configuration) =>
            Results.Text(await EntryScriptAsync(db, configuration), "text/plain; charset=utf-8"));

        boot.MapGet("/ipxe", async (HttpRequest request, AppDbContext db, IDataProtectionProvider protection, NetbootFiles files, CancellationToken ct) =>
        {
            var q = request.Query;
            var bootRequest = new BootRequest(q["mac"], q["platform"], q["manufacturer"], q["product"], q["serial"], q["uuid"], q["ip"]);
            var script = await BootService.DecideAsync(db, protection, files, bootRequest, await BootBaseAsync(db, request, ct), ct);
            return Results.Text(script, "text/plain; charset=utf-8");
        });

        boot.MapGet("/wimboot", (NetbootFiles files) =>
            files.Loader("wimboot") is { } path ? Results.File(path, "application/octet-stream") : Results.NotFound());

        boot.MapGet("/images/{id:long}/{file}", async (long id, string file, AppDbContext db, NetbootFiles files, CancellationToken ct) =>
        {
            var image = await db.BootImages.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
            return image is not null && files.ImageFile(id, image.Kind, file) is { } path
                ? Results.File(path, "application/octet-stream", enableRangeProcessing: true)
                : Results.NotFound();
        });

        boot.MapGet("/task/{token:guid}/{file}", async (Guid token, string file, HttpRequest request, AppDbContext db, IDataProtectionProvider protection, InstanceService instance, CancellationToken ct) =>
        {
            var baseUrl = await BootBaseAsync(db, request, ct);
            var serverUrl = (await instance.SettingsAsync()).PublicUrl ?? baseUrl;
            var found = await BootService.TaskFileAsync(db, protection, token, file, baseUrl, serverUrl, ct);
            return found is { } f ? Results.Text(f.Content, f.Type + "; charset=utf-8") : Results.NotFound();
        });
    }

    /// <summary>The first script with the boot address of the settings.</summary>
    public static async Task<string> EntryScriptAsync(AppDbContext db, IConfiguration configuration, CancellationToken ct = default)
    {
        var settings = await SettingsStore.LoadAsync(db, ct);
        return BootService.EntryScript(InstanceService.NormalizePublicUrl(settings.BootUrl), HttpPort(configuration));
    }

    /// <summary>The address the booting computers reach MatHub at: the boot address of the settings, else the address they asked (iPXE used the TFTP server's).</summary>
    public static async Task<string> BootBaseAsync(AppDbContext db, HttpRequest request, CancellationToken ct = default)
    {
        var settings = await SettingsStore.LoadAsync(db, ct);
        return InstanceService.NormalizePublicUrl(settings.BootUrl) ?? $"{request.Scheme}://{request.Host}";
    }

    /// <summary>The HTTP port of MatHub (from ASPNETCORE_URLS, 4910 by default): the first script reaches it at the TFTP server's address.</summary>
    public static int HttpPort(IConfiguration configuration)
    {
        if (int.TryParse(configuration["Netboot:HttpPort"], out var configured))
        {
            return configured;
        }

        var urls = configuration["ASPNETCORE_URLS"] ?? configuration["urls"] ?? string.Empty;
        var match = System.Text.RegularExpressions.Regex.Match(urls, @":(\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 4910;
    }
}
