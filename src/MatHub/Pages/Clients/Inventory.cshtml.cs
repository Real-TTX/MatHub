using System.Text.Json;
using MatHub.Data;
using MatHub.Protocol;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Clients;

/// <summary>A client, tab Inventory: hardware, operating system, disks and network adapters of the last inventory.</summary>
public sealed class InventoryModel(AppDbContext db) : ClientPageModel(db)
{
    private sealed record Stored(List<DiskInfo>? Disks, List<NetworkInfo>? Network);

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
            }
            catch (JsonException)
            {
                // an inventory MatHub cannot read is shown as none
            }
        }

        return Page();
    }
}
