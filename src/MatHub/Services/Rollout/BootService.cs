using System.Globalization;
using System.Security;
using System.Text;
using MatHub.Data;
using MatHub.Services.Agent;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services.Rollout;

/// <summary>What iPXE tells about the computer that boots (the query of <c>/boot/ipxe</c>).</summary>
public sealed record BootRequest(string? Mac, string? Platform, string? Manufacturer, string? Product, string? Serial, string? Uuid, string? Ip);

/// <summary>
/// The network boot: the iPXE scripts and the files a task needs. The chain is: the DHCP server names MatHub's TFTP server and a boot loader of MatHub (iPXE with
/// MatHub's script built in) → iPXE loads <c>mathub.ipxe</c> → which asks <c>/boot/ipxe</c> with the MAC address → a computer with a waiting task gets the
/// script of its profile (and its task turns to "installing", so the next boots go to the disk), every other computer boots from its disk.
/// Windows boots Windows PE through wimboot, which also places three files of the task into <c>X:\Windows\System32</c>: <c>winpeshl.ini</c> (runs the setup
/// script instead of the shell), <c>mathub-setup.cmd</c> (connects the installation share and starts <c>setup.exe</c>) and <c>autounattend.xml</c>. The
/// answer file installs the agent in the specialize pass with the token of the task, and the enrollment completes the task.
/// </summary>
public static class BootService
{
    /// <summary>The first script (TFTP and HTTP): asks MatHub what to boot.</summary>
    public static string EntryScript(string? bootUrl, int httpPort)
    {
        var server = bootUrl ?? $"http://${{next-server}}:{httpPort.ToString(CultureInfo.InvariantCulture)}";
        return $$"""
            #!ipxe
            # MatHub: asks MatHub what this computer boots.
            set mathub {{server}}
            chain ${mathub}/boot/ipxe?mac=${netX/mac:hexhyp}&platform=${platform}&manufacturer=${manufacturer:uristring}&product=${product:uristring}&serial=${serial:uristring}&uuid=${uuid}&ip=${netX/ip} || goto failed
            exit
            :failed
            echo MatHub cannot be reached at ${mathub}. Booting from the disk in 10 seconds.
            sleep 10
            exit

            """;
    }

    /// <summary>Boots from the disk (no task for the computer).</summary>
    public static string LocalScript(string? message) => $"""
        #!ipxe
        echo MatHub: {Clean(message)}
        sleep 3
        exit

        """;

    /// <summary>Records the computer and answers the script it boots.</summary>
    public static async Task<string> DecideAsync(AppDbContext db, IDataProtectionProvider protection, NetbootFiles files, BootRequest request, string baseUrl, CancellationToken ct = default)
    {
        if (InventoryService.NormalizeMac(request.Mac) is not { } mac)
        {
            return LocalScript("no MAC address. Booting from the disk.");
        }

        var now = DateTime.UtcNow;
        var device = await db.BootDevices.FirstOrDefaultAsync(d => d.MacAddress == mac, ct);
        if (device is null)
        {
            device = new BootDevice { MacAddress = mac };
            db.BootDevices.Add(device);
        }

        device.Platform = Cut(request.Platform, 20) ?? device.Platform;
        device.Manufacturer = Cut(request.Manufacturer, 200) ?? device.Manufacturer;
        device.Product = Cut(request.Product, 200) ?? device.Product;
        device.SerialNumber = Cut(request.Serial, 120) ?? device.SerialNumber;
        device.Uuid = Cut(request.Uuid, 60) ?? device.Uuid;
        device.IpAddress = System.Net.IPAddress.TryParse(request.Ip?.Trim(), out var ip) ? ip.ToString() : device.IpAddress;
        device.LastBootDate = now;
        device.BootCount++;

        var task = await db.RolloutTasks.Include(t => t.Profile).ThenInclude(p => p!.BootImage)
            .Where(t => t.MacAddress == mac && (t.Status == RolloutStatus.Waiting || t.Status == RolloutStatus.Installing))
            .OrderByDescending(t => t.CreateDate).FirstOrDefaultAsync(ct);
        if (task is null || task.Status == RolloutStatus.Installing || task.Profile is null)
        {
            await db.SaveChangesAsync(ct);
            return LocalScript(task is null ? $"no rollout task for {mac}. Booting from the disk." : $"{task.HostName} is being installed. Booting from the disk.");
        }

        string script;
        try
        {
            // the passwords of the profile must be readable (keys of /data lost): else no installation with an empty administrator password
            Unprotect(protection, task.Profile.AdminPasswordProtected);
            Unprotect(protection, task.Profile.SourcePasswordProtected);
            script = ProfileScript(task, task.Profile, device, files, baseUrl, protection);
        }
        catch (InvalidOperationException ex)
        {
            await db.SaveChangesAsync(ct);
            return LocalScript(ex.Message);
        }

        task.Status = RolloutStatus.Installing;
        task.BootedDate = now;
        ActivityLog.Add(db, Access.Actor.System, "rollout", "boot", task.HostName, mac);
        await db.SaveChangesAsync(ct);
        return script;
    }

    private static string ProfileScript(RolloutTask task, RolloutProfile profile, BootDevice device, NetbootFiles files, string baseUrl, IDataProtectionProvider protection)
    {
        var taskUrl = $"{baseUrl}/boot/task/{task.Token}";
        switch (profile.Kind)
        {
            case ProfileKind.Windows:
                var image = profile.BootImage ?? throw new InvalidOperationException("the profile has no boot image.");
                if (NetbootFiles.FilesOf(BootImageKind.WindowsPe).Any(f => files.ImageFile(image.Id, image.Kind, f) is null))
                {
                    throw new InvalidOperationException($"the boot image {Clean(image.Name)} is missing files.");
                }

                if (files.Loader("wimboot") is null)
                {
                    throw new InvalidOperationException("wimboot is missing on the server.");
                }

                var imageUrl = $"{baseUrl}/boot/images/{image.Id}";
                return $"""
                    #!ipxe
                    echo MatHub: installing {Clean(task.HostName)} with {Clean(profile.Name)} ...
                    kernel {baseUrl}/boot/wimboot gui
                    initrd {taskUrl}/winpeshl.ini winpeshl.ini
                    initrd {taskUrl}/mathub-setup.cmd mathub-setup.cmd
                    initrd {taskUrl}/autounattend.xml autounattend.xml
                    initrd {imageUrl}/BCD BCD
                    initrd {imageUrl}/boot.sdi boot.sdi
                    initrd {imageUrl}/boot.wim boot.wim
                    boot

                    """;
            case ProfileKind.Linux:
                var linux = profile.BootImage ?? throw new InvalidOperationException("the profile has no boot image.");
                if (NetbootFiles.FilesOf(BootImageKind.Linux).Any(f => files.ImageFile(linux.Id, linux.Kind, f) is null))
                {
                    throw new InvalidOperationException($"the boot image {Clean(linux.Name)} is missing files.");
                }

                var args = Fill(linux.KernelArguments ?? string.Empty, task, profile, baseUrl, protection);
                return $"""
                    #!ipxe
                    echo MatHub: installing {Clean(task.HostName)} with {Clean(profile.Name)} ...
                    kernel {baseUrl}/boot/images/{linux.Id}/kernel initrd=initrd {Clean(args)}
                    initrd {baseUrl}/boot/images/{linux.Id}/initrd initrd
                    boot

                    """;
            default:
                var custom = Fill(profile.IpxeScript ?? string.Empty, task, profile, baseUrl, protection).Replace("\r\n", "\n", StringComparison.Ordinal);
                return custom.StartsWith("#!ipxe", StringComparison.Ordinal) ? custom + "\n" : "#!ipxe\n" + custom + "\n";
        }
    }

    // ---- the files of a task ------------------------------------------------------------------------------------------------------

    /// <summary>A file of a running task (only while it is waiting or installing), or null.</summary>
    public static async Task<(string Content, string Type)?> TaskFileAsync(AppDbContext db, IDataProtectionProvider protection, Guid token, string file, string baseUrl, string serverUrl, CancellationToken ct = default)
    {
        var task = await db.RolloutTasks.AsNoTracking().Include(t => t.Profile)
            .FirstOrDefaultAsync(t => t.Token == token && (t.Status == RolloutStatus.Waiting || t.Status == RolloutStatus.Installing), ct);
        if (task?.Profile is not { } profile)
        {
            return null;
        }

        var platform = await db.BootDevices.AsNoTracking().Where(d => d.MacAddress == task.MacAddress).Select(d => d.Platform).FirstOrDefaultAsync(ct);
        try
        {
            return TaskFile(task, profile, protection, file, platform, serverUrl);
        }
        catch (InvalidOperationException)
        {
            // a password of the profile cannot be read: no file with an empty one
            return null;
        }
    }

    private static (string Content, string Type)? TaskFile(RolloutTask task, RolloutProfile profile, IDataProtectionProvider protection, string file, string? platform, string serverUrl)
        => file switch
        {
            "winpeshl.ini" when profile.Kind == ProfileKind.Windows => (WinPeShell(), "text/plain"),
            "mathub-setup.cmd" when profile.Kind == ProfileKind.Windows => (SetupCommand(task, profile, protection), "text/plain"),
            "autounattend.xml" when profile.Kind == ProfileKind.Windows => (string.IsNullOrWhiteSpace(profile.AnswerFile)
                ? Unattend(task, profile, platform != "pcbios", serverUrl, protection)
                : Fill(profile.AnswerFile, task, profile, serverUrl, protection, xml: true), "application/xml"),
            "answer" when profile.Kind != ProfileKind.Windows => (Fill(profile.AnswerFile ?? string.Empty, task, profile, serverUrl, protection), "text/plain"),
            "agent.ps1" when profile.InstallAgent => (AgentScript(task, serverUrl), "text/plain"),
            _ => null,
        };

    /// <summary>Windows PE runs the network start and then MatHub's setup script instead of a shell.</summary>
    public static string WinPeShell() => "[LaunchApps]\r\n%SYSTEMROOT%\\System32\\wpeinit.exe\r\n%SYSTEMROOT%\\System32\\cmd.exe, /c %SYSTEMROOT%\\System32\\mathub-setup.cmd\r\n";

    /// <summary>Connects the installation share and starts the setup with the answer file of the task. On an error it opens a prompt to look around.</summary>
    public static string SetupCommand(RolloutTask task, RolloutProfile profile, IDataProtectionProvider protection)
    {
        var password = Unprotect(protection, profile.SourcePasswordProtected);
        var credentials = string.IsNullOrEmpty(profile.SourceUser) ? string.Empty : $" /user:\"{profile.SourceUser}\" \"{password}\"";
        var lines = new[]
        {
            "@echo off",
            $"title MatHub - {CmdText(task.HostName)}",
            $"echo MatHub: installing {CmdText(task.HostName)} with {CmdText(profile.Name)}.",
            "wpeutil InitializeNetwork >nul",
            "set tries=0",
            ":connect",
            "set /a tries+=1",
            $"net use Z: \"{profile.SourcePath}\"{credentials} <nul >nul 2>&1",
            "if not errorlevel 1 goto setup",
            "if %tries% geq 12 goto failed",
            "echo MatHub: waiting for the network ...",
            "ping -n 6 127.0.0.1 >nul",
            "goto connect",
            ":setup",
            "if not exist Z:\\setup.exe goto nosetup",
            "Z:\\setup.exe /unattend:X:\\Windows\\System32\\autounattend.xml",
            "exit /b 0",
            ":nosetup",
            $"echo MatHub: there is no setup.exe in {CmdText(profile.SourcePath)}.",
            "goto shell",
            ":failed",
            $"echo MatHub: the installation share {CmdText(profile.SourcePath)} cannot be reached.",
            ":shell",
            "echo Type exit to restart the computer.",
            "cmd /k",
            "wpeutil reboot",
        };
        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>The script the answer file runs to install the agent: the install script of MatHub with the token of the task.</summary>
    public static string AgentScript(RolloutTask task, string serverUrl)
        => $"& ([scriptblock]::Create((Invoke-RestMethod -UseBasicParsing '{serverUrl}/agent/install.ps1'))) -RolloutToken '{task.Token}'\n";

    /// <summary>
    /// The answer file MatHub writes for Windows 10/11 (amd64): the whole first disk for Windows (GPT for UEFI, MBR for BIOS), the edition of the profile, the
    /// computer name, language and time zone, the password of the administrator, no questions in the OOBE, and the agent in the specialize pass (it tries for
    /// ten minutes, until the network is there).
    /// </summary>
    public static string Unattend(RolloutTask task, RolloutProfile profile, bool uefi, string serverUrl, IDataProtectionProvider protection)
    {
        static string X(string? text) => SecurityElement.Escape(text ?? string.Empty);
        const string Component = "processorArchitecture=\"amd64\" publicKeyToken=\"31bf3856ad364e35\" language=\"neutral\" versionScope=\"nonSxS\"";
        var locale = X(profile.Locale);
        var password = X(Unprotect(protection, profile.AdminPasswordProtected));
        var partitions = uefi
            ? """
                            <CreatePartitions>
                              <CreatePartition wcm:action="add"><Order>1</Order><Type>EFI</Type><Size>260</Size></CreatePartition>
                              <CreatePartition wcm:action="add"><Order>2</Order><Type>MSR</Type><Size>16</Size></CreatePartition>
                              <CreatePartition wcm:action="add"><Order>3</Order><Type>Primary</Type><Extend>true</Extend></CreatePartition>
                            </CreatePartitions>
                            <ModifyPartitions>
                              <ModifyPartition wcm:action="add"><Order>1</Order><PartitionID>1</PartitionID><Format>FAT32</Format><Label>System</Label></ModifyPartition>
                              <ModifyPartition wcm:action="add"><Order>2</Order><PartitionID>2</PartitionID></ModifyPartition>
                              <ModifyPartition wcm:action="add"><Order>3</Order><PartitionID>3</PartitionID><Format>NTFS</Format><Label>Windows</Label><Letter>C</Letter></ModifyPartition>
                            </ModifyPartitions>
              """
            : """
                            <CreatePartitions>
                              <CreatePartition wcm:action="add"><Order>1</Order><Type>Primary</Type><Size>500</Size></CreatePartition>
                              <CreatePartition wcm:action="add"><Order>2</Order><Type>Primary</Type><Extend>true</Extend></CreatePartition>
                            </CreatePartitions>
                            <ModifyPartitions>
                              <ModifyPartition wcm:action="add"><Order>1</Order><PartitionID>1</PartitionID><Format>NTFS</Format><Label>System</Label><Active>true</Active></ModifyPartition>
                              <ModifyPartition wcm:action="add"><Order>2</Order><PartitionID>2</PartitionID><Format>NTFS</Format><Label>Windows</Label><Letter>C</Letter></ModifyPartition>
                            </ModifyPartitions>
              """;
        var key = string.IsNullOrWhiteSpace(profile.ProductKey) ? string.Empty : $"<ProductKey><Key>{X(profile.ProductKey)}</Key><WillShowUI>OnError</WillShowUI></ProductKey>";
        var agent = profile.InstallAgent
            ? $"""

                  <component name="Microsoft-Windows-Deployment" {Component}>
                    <RunSynchronous>
                      <RunSynchronousCommand wcm:action="add">
                        <Order>1</Order>
                        <Description>MatHub agent</Description>
                        <Path>{X(AgentCommand(task, serverUrl))}</Path>
                      </RunSynchronousCommand>
                    </RunSynchronous>
                  </component>
              """
            : string.Empty;
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <!-- Written by MatHub for {X(task.HostName)} ({X(task.MacAddress)}). -->
            <unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State">
              <settings pass="windowsPE">
                <component name="Microsoft-Windows-International-Core-WinPE" {Component}>
                  <SetupUILanguage><UILanguage>{locale}</UILanguage></SetupUILanguage>
                  <InputLocale>{locale}</InputLocale>
                  <SystemLocale>{locale}</SystemLocale>
                  <UILanguage>{locale}</UILanguage>
                  <UserLocale>{locale}</UserLocale>
                </component>
                <component name="Microsoft-Windows-Setup" {Component}>
                  <DiskConfiguration>
                    <Disk wcm:action="add">
                      <DiskID>0</DiskID>
                      <WillWipeDisk>true</WillWipeDisk>
            {partitions}
                    </Disk>
                  </DiskConfiguration>
                  <ImageInstall>
                    <OSImage>
                      <InstallFrom><MetaData wcm:action="add"><Key>/IMAGE/INDEX</Key><Value>{profile.ImageIndex.ToString(CultureInfo.InvariantCulture)}</Value></MetaData></InstallFrom>
                      <InstallTo><DiskID>0</DiskID><PartitionID>{(uefi ? 3 : 2)}</PartitionID></InstallTo>
                    </OSImage>
                  </ImageInstall>
                  <UserData><AcceptEula>true</AcceptEula>{key}</UserData>
                </component>
              </settings>
              <settings pass="specialize">
                <component name="Microsoft-Windows-Shell-Setup" {Component}>
                  <ComputerName>{X(task.HostName)}</ComputerName>
                  <TimeZone>{X(profile.TimeZone)}</TimeZone>
                </component>{agent}
              </settings>
              <settings pass="oobeSystem">
                <component name="Microsoft-Windows-International-Core" {Component}>
                  <InputLocale>{locale}</InputLocale>
                  <SystemLocale>{locale}</SystemLocale>
                  <UILanguage>{locale}</UILanguage>
                  <UserLocale>{locale}</UserLocale>
                </component>
                <component name="Microsoft-Windows-Shell-Setup" {Component}>
                  <OOBE>
                    <HideEULAPage>true</HideEULAPage>
                    <HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>
                    <HideOnlineAccountScreens>true</HideOnlineAccountScreens>
                    <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>
                    <HideLocalAccountScreen>true</HideLocalAccountScreen>
                    <ProtectYourPC>3</ProtectYourPC>
                  </OOBE>
                  <UserAccounts>
                    <AdministratorPassword><Value>{password}</Value><PlainText>true</PlainText></AdministratorPassword>
                  </UserAccounts>
                </component>
              </settings>
            </unattend>

            """;
    }

    /// <summary>The command of the specialize pass (at most 259 characters): fetches the agent script of the task, again and again for ten minutes.</summary>
    public static string AgentCommand(RolloutTask task, string serverUrl)
        => $"powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"for($i=0;$i -lt 60;$i++){{try{{irm '{serverUrl}/boot/task/{task.Token}/agent.ps1'|iex;break}}catch{{sleep 10}}}}\"";

    /// <summary>The placeholders of answer files, kernel arguments and iPXE scripts.</summary>
    public static string Fill(string text, RolloutTask task, RolloutProfile profile, string serverUrl, IDataProtectionProvider protection, bool xml = false)
    {
        var map = new Dictionary<string, Func<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["hostname"] = () => task.HostName,
            ["mac"] = () => task.MacAddress,
            ["server"] = () => serverUrl,
            ["token"] = () => task.Token.ToString(),
            ["answer"] = () => $"{serverUrl}/boot/task/{task.Token}/answer",
            ["locale"] = () => profile.Locale,
            ["timezone"] = () => profile.TimeZone,
            ["imageindex"] = () => profile.ImageIndex.ToString(CultureInfo.InvariantCulture),
            ["productkey"] = () => profile.ProductKey ?? string.Empty,
            ["adminpassword"] = () => Unprotect(protection, profile.AdminPasswordProtected),
            ["agentcommand"] = () => AgentCommand(task, serverUrl),
        };
        var sb = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf('{', i);
            var close = open < 0 ? -1 : text.IndexOf('}', open);
            if (open < 0 || close < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }

            var name = text[(open + 1)..close];
            sb.Append(text, i, open - i);
            // in an answer file (XML) a password with & or < must not break it
            sb.Append(map.TryGetValue(name, out var value) ? xml ? System.Security.SecurityElement.Escape(value()) : value() : text[open..(close + 1)]);
            i = close + 1;
        }

        return sb.ToString();
    }

    private static string Unprotect(IDataProtectionProvider protection, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        try
        {
            return protection.CreateProtector(RolloutService.ProtectorPurpose).Unprotect(value);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw new InvalidOperationException("a password of the profile cannot be read (the keys of /data are not those it was saved with). Set it again.");
        }
    }

    /// <summary>A text for an iPXE line: no line break, no variable, no command separator, and only ASCII (the console of iPXE shows nothing else; – becomes -).</summary>
    private static string Clean(string? text) => new((text ?? string.Empty)
        .Select(c => c is '–' or '—' ? '-' : c)
        .Where(c => c < 128 && !char.IsControl(c) && c is not ('$' or '{' or '}' or '|' or '&' or '#')).ToArray());

    /// <summary>A text for an echo of cmd: none of the characters cmd reads as its own.</summary>
    private static string CmdText(string? text) => new((text ?? string.Empty).Where(c => !char.IsControl(c) && c is not ('&' or '|' or '<' or '>' or '^' or '%' or '"')).ToArray());

    private static string? Cut(string? text, int max)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > max ? t[..max] : t;
    }
}
