using System.Text;
using System.Text.Json;
using MatHub.Protocol;
using Microsoft.Win32;

namespace MatHub.Agent;

/// <summary>
/// The inventory of the computer: hardware, operating system, disks and network through CIM (Windows PowerShell, which every Windows has), the installed programs
/// from the registry (the machine's and those of the users), and what winget knows and would update.
/// </summary>
public static class Inventory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string Facts = """
        $ErrorActionPreference = 'SilentlyContinue'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        $os = Get-CimInstance Win32_OperatingSystem
        $cs = Get-CimInstance Win32_ComputerSystem
        $bios = Get-CimInstance Win32_BIOS
        $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
        $nt = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
        $disks = @(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object { [pscustomobject]@{ name = $_.DeviceID; fileSystem = $_.FileSystem; totalBytes = [int64]$_.Size; freeBytes = [int64]$_.FreeSpace } })
        $net = @(Get-CimInstance Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=True' | ForEach-Object { [pscustomobject]@{ name = $_.Description; macAddress = $_.MACAddress; addresses = @($_.IPAddress) } })
        $user = $cs.UserName
        if (-not $user) { $user = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI').LastLoggedOnUser }
        [pscustomobject]@{
            hostName = $env:COMPUTERNAME
            domain = $(if ($cs.PartOfDomain) { $cs.Domain } else { $null })
            operatingSystem = $os.Caption
            osVersion = $(if ($nt.DisplayVersion) { $nt.DisplayVersion } else { $os.Version })
            osBuild = $(if ($nt.UBR) { "$($os.BuildNumber).$($nt.UBR)" } else { $os.BuildNumber })
            architecture = $os.OSArchitecture
            manufacturer = $cs.Manufacturer
            model = $cs.Model
            serialNumber = $bios.SerialNumber
            processor = $(if ($cpu) { $cpu.Name.Trim() } else { $null })
            memoryBytes = [int64]$cs.TotalPhysicalMemory
            lastUser = $user
            bootTime = $os.LastBootUpTime.ToUniversalTime().ToString('o')
            disks = $disks
            network = $net
        } | ConvertTo-Json -Depth 4 -Compress
        """;

    public static async Task<InventoryReport> CollectAsync(CancellationToken ct)
    {
        var report = await FactsAsync(ct) ?? new InventoryReport { HostName = Environment.MachineName, OperatingSystem = Environment.OSVersion.VersionString };
        var software = Software();
        var upgrades = await Winget.UpgradesAsync(ct);
        var installed = await Winget.InstalledAsync(ct);
        return report with { Software = software, WingetUpgrades = upgrades, WingetInstalled = installed };
    }

    /// <summary>The user signed in at the console now, else the last one.</summary>
    public static string? LastUser()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI");
        return key?.GetValue("LastLoggedOnUser") as string;
    }

    public static DateTime BootTime() => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    private static async Task<InventoryReport?> FactsAsync(CancellationToken ct)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Facts));
        var result = await ProcessRunner.RunAsync(PowerShell.Path, ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded], TimeSpan.FromMinutes(2), ct);
        // the output holds what PowerShell writes on its error stream too (progress as CLIXML): the facts are the one line of JSON
        var json = result.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{') && l.EndsWith('}'));
        if (result.ExitCode != 0 || json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<InventoryReport>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The programs of the uninstall keys: the machine's (64 and 32 bit) and those of every user that is loaded; no system components and no updates.</summary>
    public static IReadOnlyList<SoftwareInfo> Software()
    {
        var found = new Dictionary<string, SoftwareInfo>(StringComparer.OrdinalIgnoreCase);
        void Read(RegistryKey? root, string path)
        {
            try
            {
                ReadKey(root, path);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // a key this account may not read (the hive of another user when the agent runs as a normal user for a test)
            }
        }

        void ReadKey(RegistryKey? root, string path)
        {
            using var key = root?.OpenSubKey(path);
            if (key is null)
            {
                return;
            }

            foreach (var name in key.GetSubKeyNames())
            {
                using var app = key.OpenSubKey(name);
                if (app?.GetValue("DisplayName") is not string display || string.IsNullOrWhiteSpace(display)
                    || app.GetValue("SystemComponent") is int and 1 || app.GetValue("ParentKeyName") is not null || app.GetValue("ReleaseType") is "Update" or "Hotfix" or "Security Update")
                {
                    continue;
                }

                var version = app.GetValue("DisplayVersion") as string;
                found.TryAdd(display.Trim() + "|" + version, new SoftwareInfo(display.Trim(), version?.Trim(), (app.GetValue("Publisher") as string)?.Trim()));
            }
        }

        const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        using (var machine64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        {
            Read(machine64, Uninstall);
        }

        using (var machine32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
        {
            Read(machine32, Uninstall);
        }

        foreach (var sid in Registry.Users.GetSubKeyNames().Where(s => s.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !s.EndsWith("_Classes", StringComparison.Ordinal)))
        {
            Read(Registry.Users, sid + "\\" + Uninstall);
        }

        return found.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>Windows PowerShell (5.1), which every Windows 10 and 11 has.</summary>
public static class PowerShell
{
    public static string Path => System.IO.Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
}
