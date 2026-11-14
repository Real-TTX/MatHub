using System.Text.Json;
using MatHub.Data;
using MatHub.Protocol;
using MatHub.Services.Clients;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services.Agent;

/// <summary>Takes the inventory an agent reports: the facts of the client, its programs (with winget's ids and updates) and its groups.</summary>
public static class InventoryService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task ApplyAsync(AppDbContext db, Client client, InventoryReport r, CancellationToken ct = default)
    {
        client.Name = Trim(r.HostName, 120) ?? client.Name;
        client.Domain = Trim(r.Domain, 200);
        client.OperatingSystem = Trim(r.OperatingSystem, 200);
        client.OsVersion = Trim(r.OsVersion, 60);
        client.OsBuild = Trim(r.OsBuild, 60);
        client.Architecture = Trim(r.Architecture, 30);
        client.Manufacturer = Trim(r.Manufacturer, 200);
        client.Model = Trim(r.Model, 200);
        client.SerialNumber = Trim(r.SerialNumber, 120);
        client.Processor = Trim(r.Processor, 200);
        client.MemoryBytes = r.MemoryBytes;
        client.LastUser = Trim(r.LastUser, 200) ?? client.LastUser;
        client.BootDate = r.BootTime?.ToUniversalTime();
        client.DiskTotalBytes = r.Disks.Count == 0 ? null : r.Disks.Sum(d => d.TotalBytes);
        client.DiskFreeBytes = r.Disks.Count == 0 ? null : r.Disks.Sum(d => d.FreeBytes);
        client.IpAddresses = Trim(string.Join(", ", r.Network.SelectMany(n => n.Addresses).Distinct()), 1000);
        client.MacAddresses = Trim(string.Join(", ", r.Network.Select(n => NormalizeMac(n.MacAddress)).Where(m => m is not null).Distinct()), 1000);
        // PostgreSQL takes no NUL in text or jsonb (a value of the registry may end with one)
        client.Inventory = JsonSerializer.Serialize(new { r.Disks, r.Network, r.BiosVersion, r.BiosDate, r.ProcessorCores, r.ProcessorThreads, r.Graphics, r.OsInstallDate, r.SecureBoot, r.Platform }, Json)
            .Replace("\\u0000", string.Empty, StringComparison.Ordinal);
        client.LastInventoryDate = DateTime.UtcNow;
        client.InventoryRequested = false;

        // the programs: those of the registry; winget adds its ids (by name) and what it would update
        var software = r.Software
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .GroupBy(s => (s.Name.Trim(), s.Version?.Trim()))
            .Select(g => new ClientSoftware { ClientId = client.Id, Name = Trim(g.Key.Item1, 300)!, Version = Trim(g.Key.Item2, 100), Publisher = Trim(g.First().Publisher, 300) })
            .ToList();
        if (r.WingetInstalled is null)
        {
            // winget could not say this time (not there, or it failed): the ids of the last inventory stay, so a package found by its id is not "missing"
            var known = await db.ClientSoftware.AsNoTracking().Where(s => s.ClientId == client.Id && s.WingetId != null).Select(s => new { s.Name, s.WingetId }).ToListAsync(ct);
            foreach (var k in known)
            {
                if (software.FirstOrDefault(s => s.WingetId is null && string.Equals(s.Name, k.Name, StringComparison.OrdinalIgnoreCase)) is { } same)
                {
                    same.WingetId = k.WingetId;
                }
            }
        }
        else if (r.WingetInstalled is { } installed)
        {
            foreach (var w in installed)
            {
                var match = software.FirstOrDefault(s => s.WingetId is null && string.Equals(s.Name, w.Name, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    match.WingetId = Trim(w.Id, 200);
                }
                else
                {
                    software.Add(new ClientSoftware { ClientId = client.Id, Name = Trim(w.Name, 300)!, Version = Trim(w.Version, 100), WingetId = Trim(w.Id, 200) });
                }
            }
        }

        if (r.WingetUpgrades is { } upgrades)
        {
            foreach (var u in upgrades)
            {
                var match = software.FirstOrDefault(s => string.Equals(s.WingetId, u.Id, StringComparison.OrdinalIgnoreCase))
                            ?? software.FirstOrDefault(s => s.WingetId is null && string.Equals(s.Name, u.Name, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    match = new ClientSoftware { ClientId = client.Id, Name = Trim(u.Name, 300)!, Version = Trim(u.Version, 100) };
                    software.Add(match);
                }

                match.WingetId = Trim(u.Id, 200);
                match.AvailableVersion = Trim(u.Available, 100);
            }
        }

        client.UpdatesAvailable = software.Count(s => s.AvailableVersion is not null);
        // the old list and the new one in one go: an error leaves the old list
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.ClientSoftware.Where(s => s.ClientId == client.Id).ExecuteDeleteAsync(ct);
            db.ClientSoftware.AddRange(software);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        await GroupService.RefreshClientAsync(db, client.Id, ct);
    }

    /// <summary>A MAC address as MatHub keeps it: lower case with colons (<c>00:1a:2b:3c:4d:5e</c>); null when it is none.</summary>
    public static string? NormalizeMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
        {
            return null;
        }

        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        return hex.Length == 12 ? string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))) : null;
    }

    private static string? Trim(string? text, int max)
    {
        var t = text?.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > max ? t[..max] : t;
    }
}
