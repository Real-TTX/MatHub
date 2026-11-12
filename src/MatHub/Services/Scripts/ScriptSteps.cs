using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MatHub.Protocol;

namespace MatHub.Services.Scripts;

/// <summary>An entry of the field editor of a dialog: a field, or (kind <c>page</c>) the start of the next page of a wizard.</summary>
public sealed record DialogFieldEntry
{
    public string Kind { get; init; } = "text";

    public string? Name { get; init; }

    public string? Label { get; init; }

    /// <summary>The choices of a choice field, separated by ;</summary>
    public string? Options { get; init; }

    public string? Default { get; init; }

    public bool Required { get; init; }
}

/// <summary>The PowerShell of one step and of one check: what both editors compile (one base, see <see cref="ScriptBlocks"/>).</summary>
public static partial class ScriptBlocks
{
    private static readonly JsonSerializerOptions DialogJson = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>The kinds of fields a dialog has.</summary>
    public static IReadOnlyList<string> DialogFieldKinds { get; } = ["text", "password", "choice", "check", "number", "multiline", "info"];

    private static readonly Dictionary<string, string> GroupSids = new(StringComparer.Ordinal)
    {
        ["administrators"] = "S-1-5-32-544",
        ["users"] = "S-1-5-32-545",
        ["power-users"] = "S-1-5-32-547",
        ["remote-desktop"] = "S-1-5-32-555",
    };

    /// <summary>The values of a block as the compiler reads them: trimmed, the default of the field when the block has none.</summary>
    private sealed class Reader(BlockType type, IReadOnlyDictionary<string, string> values, int step, List<string> errors)
    {
        public string V(string name)
            => values.TryGetValue(name, out var v) ? v.Trim() : type.Fields.FirstOrDefault(f => f.Name == name)?.Default ?? string.Empty;

        public bool Has(string name) => V(name).Length > 0;

        public bool B(string name) => V(name) == "true";

        /// <summary>The value as a PowerShell expression (a quoted string, with its variables).</summary>
        public string E(string name) => Expression(V(name));

        public string Raw(string name) => values.GetValueOrDefault(name) ?? string.Empty;

        public int Number(string name, int fallback, int min, int max)
        {
            var text = V(name);
            if (text.Length == 0)
            {
                return fallback;
            }

            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max)
            {
                return n;
            }

            Error("Blocks.Error.Number", name);
            return fallback;
        }

        /// <summary>The name of a variable the step fills (letters, digits, _).</summary>
        public string Variable(string name)
        {
            var v = V(name);
            if (!IsVariableName(v))
            {
                Error("Blocks.Error.Variable", name);
                return "answer";
            }

            return v;
        }

        /// <summary>The name of a secret (capitals, digits, _), quoted for Get-MatHubSecret.</summary>
        public string Secret(string name)
        {
            var v = V(name);
            if (!IsSecretName(v))
            {
                Error("Blocks.Error.Secret", name);
                return "''";
            }

            return Quote(v);
        }

        public void Error(string key, string field) => errors.Add(Localization.Text.T(key, step, Localization.Text.T("Blocks.Field." + field)));

        public void Fail(string text) => errors.Add(text);

        public int StepNumber => step;

        public void CheckRequired()
        {
            foreach (var f in type.Fields.Where(f => f.Required && V(f.Name).Length == 0))
            {
                Error("Blocks.Error.Missing", f.Name);
            }
        }
    }

    /// <summary>What runs in a trial run as it is: it changes nothing (the output, variables, dialogs, the end). Everything else only says what it would do.</summary>
    public static IReadOnlySet<string> DryRunSafe { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "message", "set-variable", "exit", "fail", "ui-message", "ui-question", "ui-form", "ui-notify", "ui-progress",
    };

    private static string DryRunHeader() => "$MatHubDryRun = $true\nWrite-Output " + Quote(Localization.Text.T("Scripts.Test.DryHeader")) + "\n\n";

    /// <summary>A step of a trial run: "[Trial] Join a domain – Domain: contoso.local, OU: …" with the variables put in; secrets by their name only.</summary>
    private static string DryRun(BlockType type, Reader r)
    {
        var parts = new List<string> { Quote(Localization.Text.T("Scripts.Test.DryPrefix") + " " + Localization.Text.T("Blocks.Type." + type.Id)) };
        var first = true;
        foreach (var field in type.Fields.Where(f => f.Kind != "fields" && r.Has(f.Name)))
        {
            parts.Add(Quote((first ? " – " : ", ") + Localization.Text.T("Blocks.Field." + field.Name) + ": "));
            first = false;
            parts.Add(field.Kind switch
            {
                "secret" => Quote(r.V(field.Name) + " (" + Localization.Text.T("Secrets.One") + ")"),
                "bool" => Quote(Localization.Text.T(r.B(field.Name) ? "Common.Yes" : "Common.No")),
                "select" => Quote(Localization.Text.T("Blocks.Option." + r.V(field.Name))),
                "textarea" when type.Id == "powershell" => Quote(r.V(field.Name).Split('\n')[0].Trim() + (r.V(field.Name).Contains('\n') ? " …" : string.Empty)),
                _ => r.E(field.Name),
            });
        }

        return "Write-Output (" + string.Join(" + ", parts) + ")";
    }

    private static string Step(BlockType type, IReadOnlyDictionary<string, string> values, int step, List<string> errors, bool dryRun = false)
    {
        var r = new Reader(type, values, step, errors);
        r.CheckRequired();
        if (dryRun && !DryRunSafe.Contains(type.Id))
        {
            return DryRun(type, r);
        }

        const string InstallerOk = "@(0, 3010, 1641)";
        switch (type.Id)
        {
            // ---- programs
            case "winget-install":
                return $"Invoke-MatHubWinget install --id {r.E("id")} --exact --silent --scope machine --accept-package-agreements";
            case "winget-uninstall":
                return $"Invoke-MatHubWinget uninstall --id {r.E("id")} --exact --silent";
            case "winget-upgrade":
                return r.Has("id")
                    ? $"Invoke-MatHubWinget upgrade --id {r.E("id")} --exact --silent --accept-package-agreements"
                    : "Invoke-MatHubWinget upgrade --all --silent --accept-package-agreements";
            case "msi":
                var msiArgs = r.Has("arguments") ? $" + ' ' + {r.E("arguments")}" : string.Empty;
                return $$"""
                    $p = Start-Process -FilePath 'msiexec.exe' -ArgumentList ('/i "' + {{r.E("path")}} + '" /qn /norestart'{{msiArgs}}) -Wait -PassThru
                    if ({{InstallerOk}} -notcontains $p.ExitCode) { throw "msiexec ended with $($p.ExitCode)." }
                    """;
            case "program":
                var args = r.Has("arguments") ? $" -ArgumentList {r.E("arguments")}" : string.Empty;
                return $"$p = Start-Process -FilePath {r.E("path")}{args} -Wait -PassThru -NoNewWindow\nif ($p.ExitCode -ne 0) {{ throw \"The program ended with $($p.ExitCode).\" }}";
            case "uninstall-program":
                return $$"""
                    $name = {{r.E("name")}}
                    $apps = @(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like ('*' + $name + '*') })
                    if ($apps.Count -eq 0) { Write-Output ('Not installed: ' + $name) }
                    foreach ($app in $apps) {
                        if ($app.WindowsInstaller -eq 1 -and $app.PSChildName -match '^\{[0-9A-Fa-f-]+\}$') {
                            $p = Start-Process -FilePath 'msiexec.exe' -ArgumentList ('/x ' + $app.PSChildName + ' /qn /norestart') -Wait -PassThru
                        } elseif ($app.QuietUninstallString) {
                            $p = Start-Process -FilePath 'cmd.exe' -ArgumentList ('/d /s /c "' + $app.QuietUninstallString + '"') -Wait -PassThru
                        } else {
                            throw ('There is no silent uninstall for ' + $app.DisplayName + '.')
                        }
                        if ({{InstallerOk}} -notcontains $p.ExitCode) { throw ('The uninstall of ' + $app.DisplayName + " ended with $($p.ExitCode).") }
                        Write-Output ('Removed: ' + $app.DisplayName)
                    }
                    """;

            // ---- files
            case "download":
                return $"Invoke-WebRequest -UseBasicParsing -Uri {r.E("url")} -OutFile {r.E("path")}";
            case "folder":
                return $"New-Item -ItemType Directory -Force -Path {r.E("path")} | Out-Null";
            case "copy":
                return $"Copy-Item -Path {r.E("source")} -Destination {r.E("target")} -Recurse -Force";
            case "delete":
                return $"if (Test-Path -LiteralPath {r.E("path")}) {{ Remove-Item -LiteralPath {r.E("path")} -Recurse -Force }}";
            case "write-file":
                return $"Set-Content -LiteralPath {r.E("path")} -Value {Expression(r.Raw("content"))} -Encoding UTF8";
            case "unzip":
                return $"Expand-Archive -LiteralPath {r.E("source")} -DestinationPath {r.E("target")} -Force";
            case "replace-text":
                return $$"""
                    $file = {{r.E("path")}}
                    $reader = New-Object System.IO.StreamReader($file, [Text.Encoding]::Default, $true)
                    $text = $reader.ReadToEnd(); $encoding = $reader.CurrentEncoding; $reader.Close()
                    [IO.File]::WriteAllText($file, $text.Replace({{r.E("find")}}, {{r.E("replace")}}), $encoding)
                    """;
            case "shortcut":
                return $$"""
                    $file = {{r.E("path")}}
                    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $file) | Out-Null
                    $link = (New-Object -ComObject WScript.Shell).CreateShortcut($file)
                    $link.TargetPath = {{r.E("target")}}
                    {{(r.Has("arguments") ? $"$link.Arguments = {r.E("arguments")}" : "# no arguments")}}
                    {{(r.Has("icon") ? $"$link.IconLocation = {r.E("icon")}" : "# the icon of the program")}}
                    $link.Save()
                    """;
            case "permissions":
                var rights = r.V("rights") is "Read" or "FullControl" ? r.V("rights") : "Modify";
                return $$"""
                    $item = Get-Item -LiteralPath {{r.E("path")}}
                    $inherit = if ($item -is [IO.DirectoryInfo]) { 'ContainerInherit,ObjectInherit' } else { 'None' }
                    $acl = Get-Acl -LiteralPath $item.FullName
                    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule({{r.E("account")}}, '{{rights}}', $inherit, 'None', 'Allow')))
                    Set-Acl -LiteralPath $item.FullName -AclObject $acl
                    """;

            // ---- system
            case "registry":
                var kind = r.V("type") is "DWord" or "QWord" or "ExpandString" or "MultiString" ? r.V("type") : "String";
                var value = kind switch
                {
                    "DWord" or "QWord" => long.TryParse(r.V("value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                        ? n.ToString(CultureInfo.InvariantCulture)
                        : Failed(r, "value"),
                    "MultiString" => "@(" + string.Join(", ", r.V("value").Split(';').Select(Expression)) + ")",
                    _ => r.E("value"),
                };
                return $"if (-not (Test-Path -Path {r.E("path")})) {{ New-Item -Path {r.E("path")} -Force | Out-Null }}\nNew-ItemProperty -Path {r.E("path")} -Name {r.E("name")} -PropertyType {kind} -Value {value} -Force | Out-Null";
            case "registry-delete":
                return r.Has("name")
                    ? $"Remove-ItemProperty -Path {r.E("path")} -Name {r.E("name")} -Force -ErrorAction SilentlyContinue"
                    : $"if (Test-Path -Path {r.E("path")}) {{ Remove-Item -Path {r.E("path")} -Recurse -Force }}";
            case "service":
                return r.V("action") switch
                {
                    "stop" => $"Stop-Service -Name {r.E("name")} -Force",
                    "restart" => $"Restart-Service -Name {r.E("name")} -Force",
                    "automatic" => $"Set-Service -Name {r.E("name")} -StartupType Automatic",
                    "manual" => $"Set-Service -Name {r.E("name")} -StartupType Manual",
                    "disabled" => $"Set-Service -Name {r.E("name")} -StartupType Disabled",
                    _ => $"Start-Service -Name {r.E("name")}",
                };
            case "env":
                return $"[Environment]::SetEnvironmentVariable({r.E("name")}, {r.E("value")}, 'Machine')";
            case "rename-computer":
                return $$"""
                    $newName = {{r.E("name")}}
                    if ($env:COMPUTERNAME -ieq $newName) { Write-Output ('The computer is called ' + $newName + ' already.') }
                    else { Rename-Computer -NewName $newName -Force; $MatHubVars['restartNeeded'] = 'True'; Write-Output ('Renamed to ' + $newName + ' (after the next restart).') }
                    """;
            case "timezone":
                return $"Set-TimeZone -Id {r.E("zone")}";
            case "power-plan":
                var scheme = r.V("plan") switch { "high" => "SCHEME_MIN", "saver" => "SCHEME_MAX", _ => "SCHEME_BALANCED" };
                return $"& powercfg.exe /setactive {scheme}\nif ($LASTEXITCODE -ne 0) {{ throw \"powercfg ended with $LASTEXITCODE.\" }}";
            case "scheduled-task":
                var trigger = r.V("trigger") switch
                {
                    "logon" => "New-ScheduledTaskTrigger -AtLogOn",
                    "daily" => $"New-ScheduledTaskTrigger -Daily -At {Quote(TimeOf(r))}",
                    _ => "New-ScheduledTaskTrigger -AtStartup",
                };
                var taskArgs = r.Has("arguments") ? $" -Argument {r.E("arguments")}" : string.Empty;
                return $$"""
                    $action = New-ScheduledTaskAction -Execute {{r.E("path")}}{{taskArgs}}
                    $principal = New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount -RunLevel Highest
                    Register-ScheduledTask -TaskName {{r.E("name")}} -TaskPath '\MatHub\' -Action $action -Trigger ({{trigger}}) -Principal $principal -Force | Out-Null
                    """;
            case "hosts":
                return $$"""
                    $hostsFile = Join-Path $env:windir 'System32\drivers\etc\hosts'
                    $hostName = {{r.E("host")}}
                    $lines = @(Get-Content -LiteralPath $hostsFile -ErrorAction SilentlyContinue | Where-Object { $_ -match '^\s*#' -or -not (($_.Trim() -split '\s+') -contains $hostName) })
                    Set-Content -LiteralPath $hostsFile -Value ($lines + ({{r.E("address")}} + "`t" + $hostName)) -Encoding ASCII
                    """;
            case "restart":
                return $"shutdown.exe /r /t {r.Number("delay", 60, 0, 86400)} /c 'MatHub restarts this computer.'";
            case "shutdown":
                return $"shutdown.exe /s /t {r.Number("delay", 60, 0, 86400)} /c 'MatHub shuts this computer down.'";

            // ---- network and domain
            case "domain-join":
                var reboot = r.B("reboot") ? "\n    shutdown.exe /r /t 30 /c 'MatHub restarts this computer to finish the domain join.'" : "\n    $MatHubVars['restartNeeded'] = 'True'";
                return $$"""
                    $domain = {{r.E("domain")}}
                    $cs = Get-CimInstance -ClassName Win32_ComputerSystem
                    if ($cs.PartOfDomain -and ($cs.Domain -ieq $domain)) { Write-Output ('This computer is a member of ' + $domain + ' already.') }
                    else {
                        $join = @{ DomainName = $domain; Credential = (New-MatHubCredential {{r.Secret("credential")}} ''); Force = $true }
                        {{(r.Has("ou") ? $"$join['OUPath'] = {r.E("ou")}" : "# the default container of the domain")}}
                        {{(r.Has("name") ? $"if ($env:COMPUTERNAME -ine {r.E("name")}) {{ $join['NewName'] = {r.E("name")} }}" : "# the name stays")}}
                        Add-Computer @join
                        Write-Output ('Joined ' + $domain + '.'){{reboot}}
                    }
                    """;
            case "domain-leave":
                var rebootLeave = r.B("reboot") ? "\n    shutdown.exe /r /t 30 /c 'MatHub restarts this computer to leave the domain.'" : string.Empty;
                var workgroup = r.Has("workgroup") ? r.E("workgroup") : "'WORKGROUP'";
                return $$"""
                    $cs = Get-CimInstance -ClassName Win32_ComputerSystem
                    if (-not $cs.PartOfDomain) { Write-Output 'This computer is not a member of a domain.' }
                    else {
                        $leave = @{ WorkgroupName = {{workgroup}}; Force = $true }
                        {{(r.Has("credential") ? $"$leave['UnjoinDomainCredential'] = New-MatHubCredential {r.Secret("credential")} ''" : "# without an account: the computer account stays in the domain")}}
                        Remove-Computer @leave
                        Write-Output ('Left ' + $cs.Domain + '.'){{rebootLeave}}
                    }
                    """;
            case "gpupdate":
                return "& gpupdate.exe /target:computer /force\nif ($LASTEXITCODE -ne 0) { throw \"gpupdate ended with $LASTEXITCODE.\" }";
            case "dns":
                var adapters = r.Has("adapter") ? $"Get-NetAdapter -Name {r.E("adapter")}" : "Get-NetAdapter | Where-Object Status -eq 'Up'";
                return $$"""
                    $servers = @({{r.E("servers")}} -split '[,; ]+' | Where-Object { $_ })
                    foreach ($adapter in @({{adapters}})) { Set-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -ServerAddresses $servers; Write-Output ($adapter.Name + ': ' + ($servers -join ', ')) }
                    """;
            case "wifi":
                return $$"""
                    $ssid = {{r.E("ssid")}}
                    $key = Get-MatHubSecret {{r.Secret("key")}}
                    $hex = -join ([Text.Encoding]::UTF8.GetBytes($ssid) | ForEach-Object { $_.ToString('X2') })
                    $xml = '<?xml version="1.0"?><WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1"><name>' + [Security.SecurityElement]::Escape($ssid) + '</name><SSIDConfig><SSID><hex>' + $hex + '</hex><name>' + [Security.SecurityElement]::Escape($ssid) + '</name></SSID><nonBroadcast>{{(r.B("hidden") ? "true" : "false")}}</nonBroadcast></SSIDConfig><connectionType>ESS</connectionType><connectionMode>auto</connectionMode><MSM><security><authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption><sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>' + [Security.SecurityElement]::Escape($key) + '</keyMaterial></sharedKey></security></MSM></WLANProfile>'
                    $profileFile = Join-Path $env:TEMP ('mathub-wifi-' + [guid]::NewGuid().ToString('N') + '.xml')
                    try {
                        Set-Content -LiteralPath $profileFile -Value $xml -Encoding UTF8
                        $out = & netsh.exe wlan add profile ('filename=' + $profileFile) user=all
                        if ($LASTEXITCODE -ne 0) { throw ($out -join ' ') }
                        Write-Output ('Wi-Fi ' + $ssid + ' added.')
                    } finally { Remove-Item -LiteralPath $profileFile -Force -ErrorAction SilentlyContinue }
                    """;
            case "firewall":
                var inbound = r.V("direction") != "out";
                var protocol = r.V("protocol") is "TCP" or "UDP" ? r.V("protocol") : null;
                if (r.Has("port") && protocol is null)
                {
                    r.Error("Blocks.Error.PortNeedsProtocol", "port");
                }

                return $$"""
                    $rule = @{ DisplayName = {{r.E("name")}}; Direction = '{{(inbound ? "Inbound" : "Outbound")}}'; Action = '{{(r.V("rule") == "block" ? "Block" : "Allow")}}'; Profile = 'Any'; Group = 'MatHub' }
                    {{(protocol is null ? "# every protocol" : $"$rule['Protocol'] = '{protocol}'")}}
                    {{(r.Has("port") ? $"$rule['{(inbound ? "LocalPort" : "RemotePort")}'] = @({r.E("port")} -split '[,; ]+' | Where-Object {{ $_ }})" : "# every port")}}
                    {{(r.Has("program") ? $"$rule['Program'] = {r.E("program")}" : "# every program")}}
                    Get-NetFirewallRule -DisplayName $rule.DisplayName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
                    New-NetFirewallRule @rule | Out-Null
                    """;
            case "printer":
                return $$"""
                    $printerName = {{r.E("name")}}; $address = {{r.E("address")}}; $driver = {{r.E("driver")}}
                    $port = 'IP_' + $address
                    if (-not (Get-PrinterPort -Name $port -ErrorAction SilentlyContinue)) { Add-PrinterPort -Name $port -PrinterHostAddress $address }
                    if (-not (Get-PrinterDriver -Name $driver -ErrorAction SilentlyContinue)) { Add-PrinterDriver -Name $driver }
                    if (Get-Printer -Name $printerName -ErrorAction SilentlyContinue) { Set-Printer -Name $printerName -PortName $port -DriverName $driver }
                    else { Add-Printer -Name $printerName -PortName $port -DriverName $driver }
                    """;
            case "printer-shared":
                return $$"""
                    $p = Start-Process -FilePath 'rundll32.exe' -ArgumentList ('printui.dll,PrintUIEntry /ga /n"' + {{r.E("path")}} + '"') -Wait -PassThru
                    if ($p.ExitCode -ne 0) { throw "printui ended with $($p.ExitCode)." }
                    Restart-Service -Name Spooler -Force
                    """;

            // ---- users and rights
            case "local-user":
                var fullName = r.Has("fullname") ? r.E("fullname") : "$userName";
                var admin = r.B("admin")
                    ? "\n$admins = Get-LocalGroup -SID 'S-1-5-32-544'\nif (-not (Get-LocalGroupMember -Group $admins -ErrorAction SilentlyContinue | Where-Object { $_.Name -like ('*\\' + $userName) })) { Add-LocalGroupMember -Group $admins -Member $userName }"
                    : string.Empty;
                return $$"""
                    $userName = {{r.E("name")}}
                    $password = ConvertTo-SecureString (Get-MatHubSecret {{r.Secret("password")}}) -AsPlainText -Force
                    if (Get-LocalUser -Name $userName -ErrorAction SilentlyContinue) { Set-LocalUser -Name $userName -Password $password -PasswordNeverExpires $true; Enable-LocalUser -Name $userName }
                    else { New-LocalUser -Name $userName -Password $password -PasswordNeverExpires -FullName {{fullName}} -Description 'MatHub' | Out-Null }{{admin}}
                    """;
            case "local-user-remove":
                return $"if (Get-LocalUser -Name {r.E("name")} -ErrorAction SilentlyContinue) {{ Remove-LocalUser -Name {r.E("name")} }} else {{ Write-Output ('There is no user ' + {r.E("name")} + '.') }}";
            case "local-group":
                var sid = GroupSids.GetValueOrDefault(r.V("group"), GroupSids["administrators"]);
                var add = r.V("membership") != "remove";
                return $$"""
                    $group = Get-LocalGroup -SID '{{sid}}'
                    $member = {{r.E("member")}}
                    $present = @(Get-LocalGroupMember -Group $group -ErrorAction SilentlyContinue | Where-Object { $_.Name -ieq $member -or $_.Name -like ('*\' + $member) })
                    {{(add ? "if ($present.Count -eq 0) { Add-LocalGroupMember -Group $group -Member $member } else { Write-Output ($member + ' is a member already.') }" : "if ($present.Count -gt 0) { Remove-LocalGroupMember -Group $group -Member $member } else { Write-Output ($member + ' is no member.') }")}}
                    """;

            // ---- Windows and security
            case "windows-update":
                var criteria = r.V("updates") == "drivers" ? "IsInstalled=0 and IsHidden=0 and Type='Driver'" : "IsInstalled=0 and IsHidden=0 and Type='Software'";
                var securityOnly = r.V("updates") == "security" ? "$true" : "$false";
                return $$"""
                    $session = New-Object -ComObject Microsoft.Update.Session
                    $found = $session.CreateUpdateSearcher().Search("{{criteria}}")
                    $list = New-Object -ComObject Microsoft.Update.UpdateColl
                    foreach ($u in $found.Updates) {
                        $security = @($u.Categories | Where-Object { $_.CategoryID -eq '0fa1201d-4330-4fa8-8ae9-b877473b6441' }).Count -gt 0
                        if ({{securityOnly}} -and -not $security) { Write-Output ('Left out: ' + $u.Title) }
                        else { if (-not $u.EulaAccepted) { $u.AcceptEula() }; [void]$list.Add($u); Write-Output ('Update: ' + $u.Title) }
                    }
                    if ($list.Count -eq 0) { Write-Output 'Windows is up to date.' }
                    else {
                        $downloader = $session.CreateUpdateDownloader(); $downloader.Updates = $list; [void]$downloader.Download()
                        $installer = $session.CreateUpdateInstaller(); $installer.Updates = $list; $result = $installer.Install()
                        $MatHubVars['restartNeeded'] = [string]$result.RebootRequired
                        Write-Output ("Installed (result $($result.ResultCode)); a restart is needed: $($result.RebootRequired)")
                        if ($result.ResultCode -ge 4) { throw "Windows Update ended with $($result.ResultCode)." }
                    }
                    """;
            case "windows-feature":
                return r.V("feature") == "disable"
                    ? $"$result = Disable-WindowsOptionalFeature -Online -FeatureName {r.E("name")} -NoRestart\nif ($result.RestartNeeded) {{ $MatHubVars['restartNeeded'] = 'True'; Write-Output 'A restart is needed.' }}"
                    : $"$result = Enable-WindowsOptionalFeature -Online -FeatureName {r.E("name")} -All -NoRestart\nif ($result.RestartNeeded) {{ $MatHubVars['restartNeeded'] = 'True'; Write-Output 'A restart is needed.' }}";
            case "windows-capability":
                return r.V("capability") == "remove"
                    ? $"Remove-WindowsCapability -Online -Name {r.E("name")} | Out-Null"
                    : $"Add-WindowsCapability -Online -Name {r.E("name")} | Out-Null";
            case "appx-remove":
                return $$"""
                    $pattern = {{r.E("name")}}
                    Get-AppxPackage -AllUsers -Name $pattern | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -AllUsers; Write-Output ('Removed: ' + $_.Name) }
                    Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -like $pattern } | ForEach-Object { Remove-AppxProvisionedPackage -Online -PackageName $_.PackageName | Out-Null; Write-Output ('Not for new users any more: ' + $_.DisplayName) }
                    """;
            case "bitlocker":
                var backup = r.B("backup")
                    ? "\nforeach ($k in @((Get-BitLockerVolume -MountPoint $drive).KeyProtector | Where-Object KeyProtectorType -eq 'RecoveryPassword')) { Backup-BitLockerKeyProtector -MountPoint $drive -KeyProtectorId $k.KeyProtectorId | Out-Null; Write-Output 'The recovery key is in the Active Directory.' }"
                    : string.Empty;
                return $$"""
                    $drive = {{(r.Has("drive") ? r.E("drive") : "'C:'")}}
                    $volume = Get-BitLockerVolume -MountPoint $drive
                    if ($volume.ProtectionStatus -eq 'On') { Write-Output ('BitLocker is on for ' + $drive + ' already.') }
                    else {
                        if (-not @($volume.KeyProtector | Where-Object KeyProtectorType -eq 'RecoveryPassword').Count) { Add-BitLockerKeyProtector -MountPoint $drive -RecoveryPasswordProtector | Out-Null }
                        Enable-BitLocker -MountPoint $drive -EncryptionMethod XtsAes256 -UsedSpaceOnly -TpmProtector -SkipHardwareTest | Out-Null
                        Write-Output ('BitLocker is turned on for ' + $drive + '.')
                    }{{backup}}
                    """;
            case "defender":
                return r.V("scan") switch
                {
                    "full" => "Start-MpScan -ScanType FullScan",
                    "signatures" => "Update-MpSignature",
                    _ => "Start-MpScan -ScanType QuickScan",
                };
            case "cleanup":
                return """
                    $before = (Get-PSDrive -Name C).Free
                    Remove-Item -Path (Join-Path $env:windir 'Temp\*'), 'C:\Users\*\AppData\Local\Temp\*' -Recurse -Force -ErrorAction SilentlyContinue
                    Clear-RecycleBin -Force -ErrorAction SilentlyContinue
                    Write-Output ('Freed: {0:N0} MB' -f (((Get-PSDrive -Name C).Free - $before) / 1MB))
                    """;

            // ---- dialogs
            case "ui-message":
                return $"Invoke-MatHubDialog {Dialog(r, "message")}";
            case "ui-question":
                return $"Invoke-MatHubDialog {Dialog(r, "question")}\n$MatHubVars['{r.Variable("variable")}'] = $MatHubVars['answer']";
            case "ui-form":
                return $"Invoke-MatHubDialog {Dialog(r, "form")}";
            case "ui-notify":
                return $"Invoke-MatHubDialog {Dialog(r, "notify")} -NoWait";
            case "ui-progress":
                return $"Invoke-MatHubDialog {Dialog(r, "progress")} -NoWait";

            // ---- the script
            case "set-variable":
                return $"$MatHubVars['{r.Variable("variable")}'] = {r.E("value")}";
            case "powershell":
                return r.Raw("code").Trim();
            case "message":
                return $"Write-Output {r.E("text")}";
            case "wait":
                return $"Start-Sleep -Seconds {r.Number("seconds", 10, 0, 86400)}";
            case "fail":
                return $"throw {r.E("text")}";
            case "exit":
                return $"exit {r.Number("code", 0, int.MinValue, int.MaxValue)}";
            default:
                errors.Add(Localization.Text.T("Blocks.Error.Unknown", step));
                return string.Empty;
        }
    }

    private static string Failed(Reader r, string field)
    {
        r.Error("Blocks.Error.Number", field);
        return "0";
    }

    private static string TimeOf(Reader r)
    {
        var time = r.Has("time") ? r.V("time") : "03:00";
        if (TimeOnly.TryParseExact(time, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
        {
            return t.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        r.Error("Blocks.Error.Time", "time");
        return "03:00";
    }

    /// <summary>
    /// A dialog as a quoted JSON text (the shape of <see cref="UiDialog"/>): title, text and the pages of its fields, written by the server; the variables in it
    /// (<c>{{name}}</c>) are put in by the script just before it is shown.
    /// </summary>
    private static string Dialog(Reader r, string kind)
    {
        var pages = new List<UiPage>();
        if (kind == "form")
        {
            pages = Pages(r);
        }

        var dialog = new UiDialog
        {
            Kind = kind,
            Title = r.V("title"),
            Text = r.Raw("text").Trim(),
            Level = kind == "message" && r.V("level") is "warning" or "error" ? r.V("level") : null,
            Pages = pages,
            TimeoutSeconds = r.Number("timeout", kind == "form" ? 600 : 300, 10, 86400),
        };
        return Quote(JsonSerializer.Serialize(dialog, DialogJson));
    }

    private static List<UiPage> Pages(Reader r)
    {
        List<DialogFieldEntry> entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<DialogFieldEntry>>(r.Raw("fields") is { Length: > 0 } json ? json : "[]", Json) ?? [];
        }
        catch (JsonException)
        {
            entries = [];
        }

        var pages = new List<UiPage>();
        var page = new UiPage { Title = null, Text = null, Fields = [] };
        var fields = new List<UiField>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (e.Kind == "page")
            {
                if (fields.Count > 0 || pages.Count > 0 || page.Title is not null)
                {
                    pages.Add(page with { Fields = fields });
                }

                page = new UiPage { Title = e.Label?.Trim(), Text = e.Default?.Trim() };
                fields = [];
                continue;
            }

            var kind = DialogFieldKinds.Contains(e.Kind) ? e.Kind : "text";
            var name = e.Name?.Trim();
            if (kind != "info" && (!IsVariableName(name) || !names.Add(name!)))
            {
                r.Fail(Localization.Text.T("Blocks.Error.FieldName", r.StepNumber, e.Label ?? name ?? "?"));
                continue;
            }

            var options = kind == "choice"
                ? (e.Options ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                : null;
            if (kind == "choice" && options!.Count == 0)
            {
                r.Fail(Localization.Text.T("Blocks.Error.NoChoices", r.StepNumber, e.Label ?? name ?? "?"));
            }

            fields.Add(new UiField { Name = kind == "info" ? string.Empty : name!, Label = e.Label?.Trim() ?? name ?? string.Empty, Kind = kind, Options = options, Default = e.Default, Required = e.Required });
        }

        pages.Add(page with { Fields = fields });
        if (pages.All(p => p.Fields.Count == 0))
        {
            r.Fail(Localization.Text.T("Blocks.Error.NoFields", r.StepNumber));
        }

        return pages.Where(p => p.Fields.Count > 0 || p.Title is not null).ToList();
    }

    private static string Condition(BlockCondition condition, int step, List<string> errors)
    {
        var type = Find(condition.Type);
        if (type is null || !type.IsCondition)
        {
            errors.Add(Localization.Text.T("Blocks.Error.Unknown", step));
            return "$false";
        }

        var r = new Reader(type, condition.Values, step, errors);
        r.CheckRequired();
        switch (type.Id)
        {
            case "file-exists":
                return $"Test-Path -LiteralPath {r.E("path")}";
            case "registry-exists":
                return r.Has("name")
                    ? $"$null -ne (Get-ItemProperty -Path {r.E("path")} -Name {r.E("name")} -ErrorAction SilentlyContinue)"
                    : $"Test-Path -Path {r.E("path")}";
            case "service-running":
                return $"(Get-Service -Name {r.E("name")} -ErrorAction SilentlyContinue).Status -eq 'Running'";
            case "program-installed":
                return "@(Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*', 'HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*' -ErrorAction SilentlyContinue | "
                       + $"Where-Object {{ $_.DisplayName -like ('*' + {r.E("name")} + '*') }}).Count -gt 0";
            case "os-build":
                return int.TryParse(r.V("build"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var build)
                    ? $"[Environment]::OSVersion.Version.Build -ge {build}"
                    : Failed(r, "build");
            case "variable":
                var variable = $"[string]$MatHubVars['{r.Variable("variable")}']";
                return r.V("compare") switch
                {
                    "contains" => $"{variable}.IndexOf({r.E("value")}, [StringComparison]::OrdinalIgnoreCase) -ge 0",
                    "empty" => $"[string]::IsNullOrWhiteSpace({variable})",
                    "greater" => $"({variable} -as [double]) -gt ({r.E("value")} -as [double])",
                    _ => $"{variable} -eq {r.E("value")}",
                };
            case "domain-member":
                return r.Has("domain")
                    ? $"(Get-CimInstance -ClassName Win32_ComputerSystem).Domain -ieq {r.E("domain")}"
                    : "(Get-CimInstance -ClassName Win32_ComputerSystem).PartOfDomain";
            case "computer-name":
                return $"$env:COMPUTERNAME -like {r.E("pattern")}";
            case "user-logged-on":
                return "-not [string]::IsNullOrEmpty((Get-CimInstance -ClassName Win32_ComputerSystem).UserName)";
            case "free-space":
                return $"((Get-PSDrive -Name ({(r.Has("drive") ? r.E("drive") : "'C:'")}).TrimEnd(':')).Free / 1GB) -ge {r.Number("gigabytes", 10, 0, 1000000)}";
            case "reachable":
                return $"Test-Connection -ComputerName {r.E("host")} -Count 1 -Quiet";
            case "expression":
                return r.V("code");
            default:
                return "$false";
        }
    }
}
