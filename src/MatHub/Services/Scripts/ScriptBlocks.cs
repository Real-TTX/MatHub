using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MatHub.Services.Scripts;

/// <summary>A field of a block: what the editor shows and what the compiler reads (<c>values[Name]</c>).</summary>
/// <param name="Kind">
/// text, number, select, textarea, bool ("true" or empty), secret (the name of a <see cref="MatHub.Data.Secret"/>), variable (the name of a variable the step fills),
/// fields (the fields and pages of a dialog, as JSON), file (a path; <c>./name</c> is a file of the script, chosen from its files).
/// </param>
/// <param name="Default">What a new block has in the field.</param>
public sealed record BlockField(string Name, string Kind = "text", bool Required = true, IReadOnlyList<string>? Options = null, string? Placeholder = null, string? Default = null);

/// <summary>A kind of block: a step (<see cref="IsCondition"/> false) or a check that an "If" asks (true).</summary>
public sealed record BlockType(string Id, string Category, string Icon, IReadOnlyList<BlockField> Fields, bool IsCondition = false);

/// <summary>A step of the block editor: its kind, its values, an optional condition and whether an error stops the script.</summary>
public sealed record ScriptBlock
{
    public string Type { get; init; } = string.Empty;

    public Dictionary<string, string> Values { get; init; } = [];

    public bool ContinueOnError { get; init; }

    /// <summary>Only when this check is true (or false, with <see cref="BlockCondition.Negate"/>).</summary>
    public BlockCondition? When { get; init; }
}

public sealed record BlockCondition
{
    public string Type { get; init; } = string.Empty;

    public Dictionary<string, string> Values { get; init; } = [];

    public bool Negate { get; init; }
}

/// <summary>A node of the flow editor: start, end, a step, or "if" (its check in <see cref="Condition"/>).</summary>
public sealed record FlowNode
{
    public string Id { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public Dictionary<string, string> Values { get; init; } = [];

    public BlockCondition? Condition { get; init; }

    public double X { get; init; }

    public double Y { get; init; }
}

/// <summary>An arrow of the flow: from the port of a node (next, true, false, error) to a node.</summary>
public sealed record FlowEdge
{
    public string From { get; init; } = string.Empty;

    public string To { get; init; } = string.Empty;

    public string Port { get; init; } = "next";
}

/// <summary>The model of a script made in the block or the flow editor (the column <c>Script.Model</c>).</summary>
public sealed record ScriptModel
{
    public List<ScriptBlock> Blocks { get; init; } = [];

    public List<FlowNode> Nodes { get; init; } = [];

    public List<FlowEdge> Edges { get; init; } = [];
}

/// <summary>The outcome of a compile: the PowerShell, or what is wrong (worded for the person, with the step).</summary>
public sealed record CompileResult(string Code, IReadOnlyList<string> Errors)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// The actions of the visual editors and the compiler that turns blocks and flows into PowerShell (Windows PowerShell 5.1 and later). The block editor and the
/// flow editor share it: one catalog (<see cref="Types"/>), one compiler of a step (<see cref="Step"/>) and of a check; they differ only in how the steps follow
/// each other (a list, or a graph with branches). Every value goes into the script as a single-quoted string (quotes doubled) or as a checked number, so what a
/// person types is data, never code; <c>{{name}}</c> in a value is a variable (a dialog fills it, "Set a variable" too). The exceptions are the block
/// "PowerShell" and the check "PowerShell is true", which are code by intent.
/// </summary>
public static partial class ScriptBlocks
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The areas of the catalog, in the order of the palettes.</summary>
    public static IReadOnlyList<string> Categories { get; } = ["software", "files", "system", "network", "users", "windows", "dialog", "script", "check"];

    public static IReadOnlyList<BlockType> Types { get; } =
    [
        // programs
        new("winget-install", "software", "download", [new("id", Placeholder: "Mozilla.Firefox")]),
        new("winget-uninstall", "software", "trash", [new("id", Placeholder: "Mozilla.Firefox")]),
        new("winget-upgrade", "software", "refresh", [new("id", Required: false, Placeholder: "Mozilla.Firefox")]),
        new("msi", "software", "archive", [new("path", "file", Placeholder: @"./app.msi"), new("arguments", Required: false, Placeholder: "ALLUSERS=1")]),
        new("program", "software", "play", [new("path", "file", Placeholder: @"./setup.exe"), new("arguments", Required: false, Placeholder: "/S")]),
        new("uninstall-program", "software", "trash", [new("name", Placeholder: "Adobe Acrobat Reader")]),

        // files
        new("download", "files", "download", [new("url", Placeholder: "https://example.com/file.zip"), new("path", Placeholder: @"C:\Temp\file.zip")]),
        new("folder", "files", "folder", [new("path", Placeholder: @"C:\Company")]),
        new("copy", "files", "copy", [new("source", "file", Placeholder: @"./settings.xml"), new("target", Placeholder: @"%ProgramData%\Company\settings.xml")]),
        new("delete", "files", "trash", [new("path", Placeholder: @"C:\Temp\old")]),
        new("write-file", "files", "file", [new("path", Placeholder: @"C:\Company\info.txt"), new("content", "textarea", Required: false)]),
        new("unzip", "files", "archive", [new("source", "file", Placeholder: @"./tool.zip"), new("target", Placeholder: @"C:\Tools\tool")]),
        new("replace-text", "files", "file-text", [new("path", Placeholder: @"C:\App\app.ini"), new("find", Placeholder: "Server=old"), new("replace", Required: false, Placeholder: "Server=new")]),
        new("shortcut", "files", "link", [
            new("path", Placeholder: @"C:\Users\Public\Desktop\Intranet.lnk"), new("target", Placeholder: @"C:\Program Files\App\app.exe"),
            new("arguments", Required: false), new("icon", Required: false, Placeholder: @"C:\Program Files\App\app.exe,0"),
        ]),
        new("permissions", "files", "lock", [
            new("path", Placeholder: @"C:\Company"), new("account", Placeholder: "BUILTIN\\Users"),
            new("rights", "select", Options: ["Read", "Modify", "FullControl"]),
        ]),

        // system
        new("registry", "system", "registry", [
            new("path", Placeholder: @"HKLM:\SOFTWARE\Company"), new("name", Placeholder: "Setting"),
            new("type", "select", Options: ["String", "DWord", "QWord", "ExpandString", "MultiString"]), new("value", Required: false),
        ]),
        new("registry-delete", "system", "registry", [new("path", Placeholder: @"HKLM:\SOFTWARE\Company"), new("name", Required: false, Placeholder: "Setting")]),
        new("service", "system", "settings", [new("name", Placeholder: "Spooler"), new("action", "select", Options: ["start", "stop", "restart", "automatic", "manual", "disabled"])]),
        new("env", "system", "terminal", [new("name", Placeholder: "COMPANY_HOME"), new("value", Required: false)]),
        new("rename-computer", "system", "monitor", [new("name", Placeholder: "BER-PC-{{serial}}")]),
        new("timezone", "system", "clock", [new("zone", Placeholder: "W. Europe Standard Time")]),
        new("power-plan", "system", "power", [new("plan", "select", Options: ["balanced", "high", "saver"])]),
        new("scheduled-task", "system", "calendar", [
            new("name", Placeholder: "Company cleanup"), new("path", Placeholder: @"C:\Company\cleanup.cmd"), new("arguments", Required: false),
            new("trigger", "select", Options: ["startup", "logon", "daily"]), new("time", Required: false, Placeholder: "03:00"),
        ]),
        new("hosts", "system", "globe", [new("address", Placeholder: "10.0.0.20"), new("host", Placeholder: "intranet.contoso.local")]),
        new("restart", "system", "refresh", [new("delay", "number", Required: false, Placeholder: "60")]),
        new("shutdown", "system", "power", [new("delay", "number", Required: false, Placeholder: "60")]),

        // network and domain
        new("domain-join", "network", "server", [
            new("domain", Placeholder: "contoso.local"), new("credential", "secret"), new("ou", Required: false, Placeholder: "OU=Clients,DC=contoso,DC=local"),
            new("name", Required: false, Placeholder: "BER-PC-{{serial}}"), new("reboot", "bool", Required: false, Default: "true"),
        ]),
        new("domain-leave", "network", "logout", [new("credential", "secret", Required: false), new("workgroup", Required: false, Placeholder: "WORKGROUP"), new("reboot", "bool", Required: false, Default: "true")]),
        new("gpupdate", "network", "refresh", []),
        new("dns", "network", "network", [new("servers", Placeholder: "10.0.0.10, 10.0.0.11"), new("adapter", Required: false, Placeholder: "Ethernet")]),
        new("wifi", "network", "wifi", [new("ssid", Placeholder: "Contoso"), new("key", "secret"), new("hidden", "bool", Required: false)]),
        new("firewall", "network", "shield", [
            new("name", Placeholder: "Company app"), new("direction", "select", Options: ["in", "out"]), new("protocol", "select", Options: ["TCP", "UDP", "Any"]),
            new("port", Required: false, Placeholder: "8080"), new("program", Required: false, Placeholder: @"C:\Program Files\App\app.exe"), new("rule", "select", Options: ["allow", "block"]),
        ]),
        new("printer", "network", "printer", [new("name", Placeholder: "Office 2nd floor"), new("address", Placeholder: "10.0.0.50"), new("driver", Placeholder: "Microsoft IPP Class Driver")]),
        new("printer-shared", "network", "printer", [new("path", Placeholder: @"\\printserver\Office")]),

        // users and rights
        new("local-user", "users", "user", [new("name", Placeholder: "support"), new("password", "secret"), new("fullname", Required: false, Placeholder: "Support"), new("admin", "bool", Required: false)]),
        new("local-user-remove", "users", "user", [new("name", Placeholder: "olduser")]),
        new("local-group", "users", "users", [
            new("group", "select", Options: ["administrators", "users", "remote-desktop", "power-users"]), new("member", Placeholder: @"CONTOSO\Helpdesk"),
            new("membership", "select", Options: ["add", "remove"]),
        ]),

        // Windows and security
        new("windows-update", "windows", "refresh", [new("updates", "select", Options: ["software", "security", "drivers"])]),
        new("windows-feature", "windows", "layers", [new("name", Placeholder: "NetFx3"), new("feature", "select", Options: ["enable", "disable"])]),
        new("windows-capability", "windows", "layers", [new("name", Placeholder: "Rsat.ActiveDirectory.DS-LDS.Tools~~~~0.0.1.0"), new("capability", "select", Options: ["add", "remove"])]),
        new("appx-remove", "windows", "trash", [new("name", Placeholder: "Microsoft.BingNews")]),
        new("bitlocker", "windows", "lock", [new("drive", Required: false, Placeholder: "C:", Default: "C:"), new("backup", "bool", Required: false)]),
        new("defender", "windows", "shield", [new("scan", "select", Options: ["quick", "full", "signatures"])]),
        new("cleanup", "windows", "broom", []),

        // dialogs: the person at the computer sees them (the kiosk of the agent shows them)
        new("ui-message", "dialog", "message-square", [new("title", Placeholder: "IT"), new("text", "textarea"), new("level", "select", Options: ["info", "warning", "error"]), new("timeout", "number", Required: false, Placeholder: "300")]),
        new("ui-question", "dialog", "help-circle", [new("title", Placeholder: "IT"), new("text", "textarea", Placeholder: "Restart now?"), new("variable", "variable", Default: "answer"), new("timeout", "number", Required: false, Placeholder: "300")]),
        new("ui-form", "dialog", "list-checks", [new("title", Placeholder: "Set up this computer"), new("text", "textarea", Required: false), new("fields", "fields"), new("timeout", "number", Required: false, Placeholder: "600")]),
        new("ui-notify", "dialog", "bell", [new("title", Placeholder: "IT"), new("text")]),
        new("ui-progress", "dialog", "progress-play", [new("title", Placeholder: "Setting up"), new("text", Required: false, Placeholder: "Programs are being installed …")]),

        // the script itself
        new("set-variable", "script", "tag", [new("variable", "variable"), new("value", Required: false)]),
        new("powershell", "script", "terminal", [new("code", "textarea", Placeholder: "Get-Service | Where-Object Status -eq 'Running'")]),
        new("message", "script", "message-square", [new("text")]),
        new("wait", "script", "clock", [new("seconds", "number", Placeholder: "10")]),
        new("fail", "script", "x-circle", [new("text", Placeholder: "This computer is not supported.")]),
        new("exit", "script", "stop", [new("code", "number", Required: false, Placeholder: "0")]),

        // checks (for "if" and "only if")
        new("file-exists", "check", "file", [new("path", Placeholder: @"C:\Program Files\App\app.exe")], IsCondition: true),
        new("registry-exists", "check", "registry", [new("path", Placeholder: @"HKLM:\SOFTWARE\Company"), new("name", Required: false)], IsCondition: true),
        new("service-running", "check", "settings", [new("name", Placeholder: "Spooler")], IsCondition: true),
        new("program-installed", "check", "box", [new("name", Placeholder: "Firefox")], IsCondition: true),
        new("os-build", "check", "monitor", [new("build", "number", Placeholder: "22631")], IsCondition: true),
        new("variable", "check", "tag", [new("variable", "variable", Default: "answer"), new("compare", "select", Options: ["equals", "contains", "empty", "greater"]), new("value", Required: false, Placeholder: "yes")], IsCondition: true),
        new("domain-member", "check", "server", [new("domain", Required: false, Placeholder: "contoso.local")], IsCondition: true),
        new("computer-name", "check", "monitor", [new("pattern", Placeholder: "BER-*")], IsCondition: true),
        new("user-logged-on", "check", "user", [], IsCondition: true),
        new("free-space", "check", "hard-drive", [new("drive", Placeholder: "C:", Default: "C:"), new("gigabytes", "number", Placeholder: "20")], IsCondition: true),
        new("reachable", "check", "network", [new("host", Placeholder: "intranet.contoso.local")], IsCondition: true),
        new("expression", "check", "terminal", [new("code", Placeholder: "(Get-Date).DayOfWeek -eq 'Monday'")], IsCondition: true),
    ];

    public static BlockType? Find(string? id) => Types.FirstOrDefault(t => t.Id == id);

    public static ScriptModel Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ScriptModel();
        }

        try
        {
            return JsonSerializer.Deserialize<ScriptModel>(json, Json) ?? new ScriptModel();
        }
        catch (JsonException)
        {
            return new ScriptModel();
        }
    }

    public static string Serialize(ScriptModel model) => JsonSerializer.Serialize(model, Json);

    /// <summary>
    /// A PowerShell string literal: single quotes, quotes doubled (nothing in it is expanded). PowerShell also takes the typographic single quotes
    /// (U+2018 to U+201B) as quotes, so they are doubled too.
    /// </summary>
    public static string Quote(string? value)
    {
        var sb = new StringBuilder("'");
        foreach (var c in value ?? string.Empty)
        {
            sb.Append(c);
            if (c is '\'' or '\u2018' or '\u2019' or '\u201a' or '\u201b')
            {
                sb.Append(c);
            }
        }

        return sb.Append('\'').ToString();
    }

    /// <summary>
    /// A value as a PowerShell expression (see <see cref="Text"/>): <c>./name</c> at its start is a file of the script (in the folder the run downloads them to),
    /// <c>%ProgramData%</c> and the other variables of Windows are put in on the computer.
    /// </summary>
    public static string Expression(string? value)
    {
        value ??= string.Empty;
        var file = value.StartsWith("./", StringComparison.Ordinal) || value.StartsWith(@".\", StringComparison.Ordinal);
        var expression = Text(file ? value[2..] : value);
        if (file)
        {
            expression = $"(Join-Path $MatHubFiles {expression})";
        }

        return WindowsVariableRegex().IsMatch(value) ? $"([Environment]::ExpandEnvironmentVariables({expression}))" : expression;
    }

    /// <summary>
    /// A text as a PowerShell expression: a quoted string, or, when it names variables (<c>{{name}}</c>), the quoted parts joined with the variables
    /// (<c>('BER-' + [string]$MatHubVars['serial'])</c>). A name is letters, digits and _ only, so nothing else of the text becomes code.
    /// </summary>
    private static string Text(string value)
    {
        var matches = VariableRegex().Matches(value);
        if (matches.Count == 0)
        {
            return Quote(value);
        }

        var parts = new List<string>();
        var at = 0;
        foreach (Match m in matches)
        {
            if (m.Index > at)
            {
                parts.Add(Quote(value[at..m.Index]));
            }

            parts.Add($"[string]$MatHubVars['{m.Groups[1].Value}']");
            at = m.Index + m.Length;
        }

        if (at < value.Length)
        {
            parts.Add(Quote(value[at..]));
        }

        return "(" + string.Join(" + ", parts) + ")";
    }

    /// <summary>A name of a variable: a letter, then letters, digits and _ (at most 40).</summary>
    public static bool IsVariableName(string? name) => name is not null && VariableNameRegex().IsMatch(name);

    /// <summary>A name of a secret: capitals, digits and _ (it is part of an environment variable).</summary>
    public static bool IsSecretName(string? name) => name is not null && SecretNameRegex().IsMatch(name);

    /// <summary>The environment variable a secret reaches the script in.</summary>
    public static string SecretVariable(string name) => "MATHUB_SECRET_" + name;

    /// <summary>
    /// The secrets a script may name (<c>$env:MATHUB_SECRET_NAME</c>, <c>Get-MatHubSecret 'NAME'</c>): the server hands those that exist to the agent with the
    /// run, nothing else. <c>…_USER</c> is the user name of a secret, so the name without it counts too.
    /// </summary>
    public static IReadOnlySet<string> SecretsIn(string? script)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in SecretUseRegex().Matches(script ?? string.Empty))
        {
            var name = m.Groups[1].Value;
            names.Add(name);
            if (name.EndsWith("_USER", StringComparison.Ordinal) && name.Length > 5)
            {
                names.Add(name[..^5]);
            }
        }

        return names;
    }

    /// <summary>The code without the helpers every visual script begins with (the preview shows what the steps do).</summary>
    public static string WithoutPrelude(string code)
        => code.StartsWith(Prelude, StringComparison.Ordinal)
            ? "# … MatHub: variables ($MatHubVars), winget, dialogs, secrets (in the saved script)\n\n" + code[Prelude.Length..]
            : code;

    private const string Prelude = """
        # Made with the visual editor of MatHub. Edit it there: changes here are overwritten.
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'

        # the variables of the script ({{name}} in a value); a dialog and "Set a variable" fill them
        $MatHubVars = @{}
        # the files of the script: the folder of the run (./name in a field)
        $MatHubFiles = if ($env:MATHUB_FILES) { $env:MATHUB_FILES } else { (Get-Location).Path }
        $MatHubVars['computer'] = $env:COMPUTERNAME
        $MatHubVars['date'] = (Get-Date).ToString('yyyy-MM-dd')
        try {
            $cs = Get-CimInstance -ClassName Win32_ComputerSystem
            $MatHubVars['user'] = [string]$cs.UserName
            $MatHubVars['domain'] = [string]$cs.Domain
            $MatHubVars['serial'] = [string](Get-CimInstance -ClassName Win32_BIOS).SerialNumber
        } catch { }

        function Invoke-MatHubWinget {
            if (-not $env:MATHUB_WINGET) { throw 'winget is not available on this computer.' }
            $verb = [string]$args[0]
            & $env:MATHUB_WINGET @args --accept-source-agreements --disable-interactivity
            $code = $LASTEXITCODE
            # -1978335216 = no installer for the whole machine (a package for one user): once more without --scope machine
            if ($code -eq -1978335216 -and $args -contains '--scope') {
                $rest = @(); $skip = $false
                foreach ($a in $args) { if ($skip) { $skip = $false } elseif ($a -eq '--scope') { $skip = $true } else { $rest += $a } }
                & $env:MATHUB_WINGET @rest --accept-source-agreements --disable-interactivity
                $code = $LASTEXITCODE
            }
            # 0 = done; -1978335189 = nothing to update; -1978335135 = installed already; -1978335212 = no package found: fine only for an uninstall
            $ok = @(0, -1978335189, -1978335135)
            if ($verb -eq 'uninstall') { $ok += -1978335212 }
            if ($ok -notcontains $code) { throw "winget ended with $code." }
        }

        # a dialog for the person at the computer: the agent shows it in their session and gives back the answer (and the values of the fields)
        function Invoke-MatHubDialog([string]$Json, [switch]$NoWait) {
            $Json = [regex]::Replace($Json, '\{\{\s*([A-Za-z][A-Za-z0-9_]{0,39})\s*\}\}', [Text.RegularExpressions.MatchEvaluator]{
                param($m) $j = ConvertTo-Json -InputObject ([string]$MatHubVars[$m.Groups[1].Value]) -Compress; $j.Substring(1, $j.Length - 2)
            })
            $reply = $null
            if ($env:MATHUB_AGENT_EXE -and (Test-Path -LiteralPath $env:MATHUB_AGENT_EXE)) {
                $data = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Json))
                $arguments = @('ask', '--data', $data)
                if ($NoWait) { $arguments += '--no-wait' }
                $out = & $env:MATHUB_AGENT_EXE @arguments
                try { $reply = ($out | Out-String) | ConvertFrom-Json } catch { $reply = $null }
            }
            if ($null -eq $reply) { $reply = [pscustomobject]@{ answer = 'nobody'; values = $null } }
            # a notice or "please wait" answers nothing: the answer of the question before stays
            if ($NoWait) { return }
            $MatHubVars['answer'] = [string]$reply.answer
            if ($null -ne $reply.values) {
                # only the fields of this dialog: an answer cannot set another variable of the script
                $fields = @()
                try { $fields = @(($Json | ConvertFrom-Json).pages | ForEach-Object { $_.fields } | ForEach-Object { [string]$_.name }) } catch { }
                foreach ($p in $reply.values.PSObject.Properties) { if ($fields -contains $p.Name) { $MatHubVars[$p.Name] = [string]$p.Value } }
            }
            Write-Output ("Dialog: " + $reply.answer)
            return
        }

        function Get-MatHubSecret([string]$Name) {
            $value = [Environment]::GetEnvironmentVariable('MATHUB_SECRET_' + $Name)
            if (-not $value) { throw "The secret $Name did not reach this computer (is it still there in MatHub?)." }
            return $value
        }

        function New-MatHubCredential([string]$Name, [string]$User) {
            if (-not $User) { $User = [Environment]::GetEnvironmentVariable('MATHUB_SECRET_' + $Name + '_USER') }
            if (-not $User) { throw "The secret $Name has no user name." }
            New-Object System.Management.Automation.PSCredential($User, (ConvertTo-SecureString (Get-MatHubSecret $Name) -AsPlainText -Force))
        }

        """;

    // ---- the block editor ----------------------------------------------------------------------------------------------------

    /// <param name="dryRun">A trial run: what would change the computer only says so (see <see cref="DryRunSafe"/>).</param>
    public static CompileResult CompileBlocks(ScriptModel model, bool dryRun = false)
    {
        var errors = new List<string>();
        var sb = new StringBuilder(Prelude).Append(dryRun ? DryRunHeader() : string.Empty);
        if (model.Blocks.Count == 0)
        {
            errors.Add(Localization.Text.T("Blocks.Error.Empty"));
        }

        for (var i = 0; i < model.Blocks.Count; i++)
        {
            var block = model.Blocks[i];
            var step = i + 1;
            var type = Find(block.Type);
            if (type is null || type.IsCondition)
            {
                errors.Add(Localization.Text.T("Blocks.Error.Unknown", step));
                continue;
            }

            var code = Step(type, block.Values, step, errors, dryRun);
            sb.Append("# ").Append(step.ToString(CultureInfo.InvariantCulture)).Append(". ").AppendLine(Escape(Localization.Text.T("Blocks.Type." + type.Id)));
            if (block.When is { } when)
            {
                var test = Condition(when, step, errors);
                code = $"if ({(when.Negate ? "-not " : string.Empty)}({test})) {{\n{Indent(code)}\n}}";
            }

            if (block.ContinueOnError)
            {
                code = $"try {{\n{Indent(code)}\n}} catch {{\n    Write-Warning (\"Step {step}: \" + $_)\n}}";
            }

            sb.AppendLine(code).AppendLine();
        }

        return new CompileResult(sb.ToString().TrimEnd() + "\n", errors);
    }

    // ---- the flow editor -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// A flow becomes a small state machine: <c>$step</c> names the node that runs next, each node sets the next one by its arrows (an "if" by its result, a step
    /// with an error arrow by its outcome), and a node without an arrow ends the script. Loops are allowed; 10,000 steps end a script that never stops.
    /// </summary>
    public static CompileResult CompileFlow(ScriptModel model, bool dryRun = false)
    {
        var errors = new List<string>();
        var sb = new StringBuilder(Prelude).Append(dryRun ? DryRunHeader() : string.Empty);
        var nodes = model.Nodes.Where(n => !string.IsNullOrWhiteSpace(n.Id)).GroupBy(n => n.Id).Select(g => g.First()).ToList();
        var starts = nodes.Where(n => n.Type == "start").ToList();
        if (starts.Count != 1)
        {
            errors.Add(Localization.Text.T("Flow.Error.Start"));
            return new CompileResult(sb.ToString(), errors);
        }

        string? Next(string from, string port) => model.Edges.FirstOrDefault(e => e.From == from && e.Port == port && nodes.Any(n => n.Id == e.To))?.To;
        string Target(string? id) => id is null ? "$null" : Quote(id);

        var first = Next(starts[0].Id, "next");
        if (first is null)
        {
            errors.Add(Localization.Text.T("Flow.Error.NoFirst"));
        }

        sb.AppendLine($"$step = {Target(first)}");
        sb.AppendLine("$guard = 0");
        sb.AppendLine("while ($null -ne $step) {");
        sb.AppendLine("    if (++$guard -gt 10000) { throw 'The flow ran more than 10000 steps.' }");
        sb.AppendLine("    switch ($step) {");
        var number = 0;
        foreach (var node in nodes.Where(n => n.Type != "start"))
        {
            number++;
            var label = node.Type switch
            {
                "end" => Localization.Text.T("Flow.End"),
                "if" => Localization.Text.T("Flow.If"),
                _ => Localization.Text.T("Blocks.Type." + node.Type),
            };
            string body;
            if (node.Type == "end")
            {
                var exit = node.Values.GetValueOrDefault("code");
                body = int.TryParse(exit, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) && code != 0 ? $"exit {code}" : "$step = $null";
            }
            else if (node.Type == "if")
            {
                if (node.Condition is not { } condition)
                {
                    errors.Add(Localization.Text.T("Flow.Error.NoCheck", number));
                    continue;
                }

                var test = Condition(condition, number, errors);
                body = $"if ({(condition.Negate ? "-not " : string.Empty)}({test})) {{ $step = {Target(Next(node.Id, "true"))} }} else {{ $step = {Target(Next(node.Id, "false"))} }}";
            }
            else
            {
                var type = Find(node.Type);
                if (type is null || type.IsCondition)
                {
                    errors.Add(Localization.Text.T("Blocks.Error.Unknown", number));
                    continue;
                }

                var code = Step(type, node.Values, number, errors, dryRun);
                var onError = Next(node.Id, "error");
                body = onError is null
                    ? $"{code}\n$step = {Target(Next(node.Id, "next"))}"
                    : $"try {{\n{Indent(code)}\n    $step = {Target(Next(node.Id, "next"))}\n}} catch {{\n    Write-Warning (\"{Escape(label)}: \" + $_)\n    $step = {Target(onError)}\n}}";
            }

            sb.AppendLine($"        # {Escape(label)}");
            sb.AppendLine($"        {Quote(node.Id)} {{");
            sb.AppendLine(Indent(body, 12));
            sb.AppendLine("        }");
        }

        sb.AppendLine("        default { throw \"Unknown step $step.\" }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return new CompileResult(sb.ToString(), errors);
    }

    /// <summary>
    /// The blocks as a flow (one base, two views): every step a node under the one before, a step with "only if" an "If" whose "no" goes on to the next step,
    /// a step that goes on after an error an error arrow to the next one.
    /// </summary>
    public static ScriptModel BlocksToFlow(ScriptModel blocks)
    {
        var nodes = new List<FlowNode> { new() { Id = "start", Type = "start", X = 60, Y = 40 } };
        var edges = new List<FlowEdge>();
        var y = 40.0;
        var pending = new List<(string From, string Port)> { ("start", "next") };
        var i = 0;
        foreach (var block in blocks.Blocks)
        {
            i++;
            y += 114;
            var ifId = $"if{i}";
            var stepId = $"s{i}";
            if (block.When is { } when)
            {
                nodes.Add(new FlowNode { Id = ifId, Type = "if", Condition = when, X = 60, Y = y });
                edges.AddRange(pending.Select(p => new FlowEdge { From = p.From, To = ifId, Port = p.Port }));
                pending = [(ifId, "false")];
                y += 114;
                nodes.Add(new FlowNode { Id = stepId, Type = block.Type, Values = new(block.Values), X = 60, Y = y });
                edges.Add(new FlowEdge { From = ifId, To = stepId, Port = "true" });
            }
            else
            {
                nodes.Add(new FlowNode { Id = stepId, Type = block.Type, Values = new(block.Values), X = 60, Y = y });
                edges.AddRange(pending.Select(p => new FlowEdge { From = p.From, To = stepId, Port = p.Port }));
                pending = [];
            }

            pending.Add((stepId, "next"));
            if (block.ContinueOnError)
            {
                pending.Add((stepId, "error"));
            }
        }

        y += 114;
        nodes.Add(new FlowNode { Id = "end", Type = "end", X = 60, Y = y });
        edges.AddRange(pending.Select(p => new FlowEdge { From = p.From, To = "end", Port = p.Port }));
        return new ScriptModel { Nodes = nodes, Edges = edges };
    }

    /// <summary>
    /// Indents code for reading – but never a line that begins inside a string: the content of a multi-line value or of a here-string would change (and a
    /// here-string ends only with its terminator at the very start of a line).
    /// </summary>
    private static string Indent(string code, int spaces = 4)
    {
        var pad = new string(' ', spaces);
        var inside = LinesInStrings(code);
        return string.Join('\n', code.Split('\n').Select((l, i) => l.Length == 0 || inside.Contains(i) ? l : pad + l));
    }

    /// <summary>The numbers of the lines that begin inside a string of PowerShell ('…', "…", @'…'@, @"…"@; the typographic quotes count as quotes too).</summary>
    internal static HashSet<int> LinesInStrings(string code)
    {
        const string singles = "'‘’‚‛";
        const string doubles = "\"“”„";
        var lines = new HashSet<int>();
        var line = 0;
        var mode = 0; // 0 code, 1 '…', 2 "…", 3 @'…'@, 4 @"…"@, 5 <#…#>
        var comment = false;
        char Next(int i) => i + 1 < code.Length ? code[i + 1] : '\0';
        bool LineStart(int i) => i == 0 || code[i - 1] == '\n';
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (c == '\n')
            {
                line++;
                comment = false;
                if (mode is >= 1 and <= 4)
                {
                    lines.Add(line);
                }

                continue;
            }

            if (comment)
            {
                continue;
            }

            switch (mode)
            {
                case 0:
                    if (c == '`')
                    {
                        i++;
                    }
                    else if (c == '#' && (i == 0 || char.IsWhiteSpace(code[i - 1]) || code[i - 1] is ';' or '{' or '}' or '(' or ')'))
                    {
                        comment = true;
                    }
                    else if (c == '<' && Next(i) == '#')
                    {
                        mode = 5;
                        i++;
                    }
                    else if (c == '@' && singles.Contains(Next(i)))
                    {
                        mode = 3;
                        i++;
                    }
                    else if (c == '@' && doubles.Contains(Next(i)))
                    {
                        mode = 4;
                        i++;
                    }
                    else if (singles.Contains(c))
                    {
                        mode = 1;
                    }
                    else if (doubles.Contains(c))
                    {
                        mode = 2;
                    }

                    break;
                case 1:
                    if (singles.Contains(c))
                    {
                        if (singles.Contains(Next(i)))
                        {
                            i++;
                        }
                        else
                        {
                            mode = 0;
                        }
                    }

                    break;
                case 2:
                    if (c == '`')
                    {
                        i++;
                    }
                    else if (doubles.Contains(c))
                    {
                        if (doubles.Contains(Next(i)))
                        {
                            i++;
                        }
                        else
                        {
                            mode = 0;
                        }
                    }

                    break;
                case 3 or 4:
                    if (LineStart(i) && (mode == 3 ? singles : doubles).Contains(c) && Next(i) == '@')
                    {
                        mode = 0;
                        i++;
                    }

                    break;
                case 5:
                    if (c == '#' && Next(i) == '>')
                    {
                        mode = 0;
                        i++;
                    }

                    break;
            }
        }

        return lines;
    }

    /// <summary>A text inside a double-quoted PowerShell string and a comment: no quote, no $, no backtick, one line.</summary>
    private static string Escape(string text) => new(text.Where(c => c is not ('"' or '$' or '`' or '\n' or '\r')).ToArray());

    [GeneratedRegex(@"\{\{\s*([A-Za-z][A-Za-z0-9_]{0,39})\s*\}\}")]
    private static partial Regex VariableRegex();

    [GeneratedRegex(@"%[A-Za-z_][A-Za-z0-9_()]*%")]
    private static partial Regex WindowsVariableRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,39}$")]
    private static partial Regex VariableNameRegex();

    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{0,59}$")]
    private static partial Regex SecretNameRegex();

    [GeneratedRegex(@"(?:MATHUB_SECRET_|Get-MatHubSecret\s+'|New-MatHubCredential\s+')([A-Z][A-Z0-9_]{0,59})")]
    private static partial Regex SecretUseRegex();
}
