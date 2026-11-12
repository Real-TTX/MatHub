using System.Text.Json;
using MatHub.Data;
using MatHub.Protocol;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Clients;

/// <summary>A client, tab Inventory: hardware, operating system, disks and network adapters of the last inventory.</summary>
public sealed class InventoryModel(AppDbContext db) : ClientPageModel(db)
{
    private sealed record Stored(List<DiskInfo>? Disks, List<NetworkInfo>? Network, string? BiosVersion, DateTime? BiosDate, int? ProcessorCores, int? ProcessorThreads,
        List<string>? Graphics, DateTime? OsInstallDate, bool? SecureBoot);

    /// <summary>What the inventory says about the hardware beyond the columns of the client.</summary>
    public sealed record HardwareFacts(string? BiosVersion, DateTime? BiosDate, int? ProcessorCores, int? ProcessorThreads, IReadOnlyList<string> Graphics, DateTime? OsInstallDate, bool? SecureBoot);

    public HardwareFacts? Hardware { get; private set; }

    /// <summary>"3 d 4 h", "5 h 12 min" since a moment.</summary>
    public string Uptime(DateTime bootUtc)
    {
        var span = DateTime.UtcNow - DateTime.SpecifyKind(bootUtc, DateTimeKind.Utc);
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalDays >= 1 ? $"{(int)span.TotalDays} d {span.Hours} h" : $"{span.Hours} h {span.Minutes} min";
    }

    public IReadOnlyList<DiskInfo> Disks { get; private set; } = [];

    public IReadOnlyList<NetworkInfo> Network { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadClientAsync())
        {
            return NotFound();
        }

        if (!string.IsNullOrEmpty(Client.Inventory))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<Stored>(Client.Inventory, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                Disks = stored?.Disks ?? [];
                Network = stored?.Network ?? [];
                if (stored is not null)
                {
                    Hardware = new HardwareFacts(stored.BiosVersion, stored.BiosDate, stored.ProcessorCores, stored.ProcessorThreads, stored.Graphics ?? [], stored.OsInstallDate, stored.SecureBoot);
                }
            }
            catch (JsonException)
            {
                // an inventory MatHub cannot read is shown as none
            }
        }

        return Page();
    }
}
