using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MatHub.Services.Scripts;

/// <summary>A field of a block: what the editor shows and what the compiler reads (<c>values[Name]</c>).</summary>
/// <param name="Kind">text, number, select, textarea.</param>
public sealed record BlockField(string Name, string Kind = "text", bool Required = true, IReadOnlyList<string>? Options = null, string? Placeholder = null);

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
/// The building blocks of the visual editors and the compiler that turns blocks and flows into PowerShell (Windows PowerShell 5.1 and later). Every value
/// goes into the script as a single-quoted string (quotes doubled) or as a checked number, so what a person types is data, never code; the one exception is
/// the block "PowerShell", which is code by intent.
/// </summary>
public static class ScriptBlocks
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IReadOnlyList<BlockType> Types { get; } =
    [
        // programs
        new("winget-install", "software", "download", [new("id", Placeholder: "Mozilla.Firefox")]),
        new("winget-uninstall", "software", "trash", [new("id", Placeholder: "Mozilla.Firefox")]),
        new("winget-upgrade", "software", "refresh", [new("id", Required: false, Placeholder: "Mozilla.Firefox")]),
        new("program", "software", "play", [new("path", Placeholder: @"C:\Tools\setup.exe"), new("arguments", Required: false, Placeholder: "/S")]),

        // files
        new("download", "files", "download", [new("url", Placeholder: "https://example.com/file.zip"), new("path", Placeholder: @"C:\Temp\file.zip")]),
        new("folder", "files", "folder", [new("path", Placeholder: @"C:\Company")]),
        new("copy", "files", "copy", [new("source", Placeholder: @"C:\Temp\a.txt"), new("target", Placeholder: @"C:\Company\a.txt")]),
        new("delete", "files", "trash", [new("path", Placeholder: @"C:\Temp\old")]),
        new("write-file", "files", "file", [new("path", Placeholder: @"C:\Company\info.txt"), new("content", "textarea", Required: false)]),

        // system
        new("registry", "system", "database", [
            new("path", Placeholder: @"HKLM:\SOFTWARE\Company"), new("name", Placeholder: "Setting"),
            new("type", "select", Options: ["String", "DWord", "QWord", "ExpandString", "MultiString"]), new("value", Required: false),
        ]),
        new("registry-delete", "system", "database", [new("path", Placeholder: @"HKLM:\SOFTWARE\Company"), new("name", Required: false, Placeholder: "Setting")]),
        new("service", "system", "settings", [new("name", Placeholder: "Spooler"), new("action", "select", Options: ["start", "stop", "restart", "automatic", "manual", "disabled"])]),
        new("env", "system", "terminal", [new("name", Placeholder: "COMPANY_HOME"), new("value", Required: false)]),
        new("restart", "system", "refresh", [new("delay", "number", Required: false, Placeholder: "60")]),

        // flow
        new("powershell", "script", "terminal", [new("code", "textarea", Placeholder: "Get-Service | Where-Object Status -eq 'Running'")]),
        new("message", "script", "message-square", [new("text")]),
        new("wait", "script", "clock", [new("seconds", "number", Placeholder: "10")]),
        new("exit", "script", "stop", [new("code", "number", Required: false, Placeholder: "0")]),

        // checks (for "if" and "only if")
        new("file-exists", "check", "file", [new("path", Placeholder: @"C:\Program Files\App\app.exe")], IsCondition: true),
        new("registry-exists", "check", "database", [new("path", Placeholder: @"HKLM:\SOFTWARE\Company"), new("name", Required: false)], IsCondition: true),
        new("service-running", "check", "settings", [new("name", Placeholder: "Spooler")], IsCondition: true),
        new("program-installed", "check", "box", [new("name", Placeholder: "Firefox")], IsCondition: true),
        new("os-build", "check", "monitor", [new("build", "number", Placeholder: "22631")], IsCondition: true),
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

    private const string Prelude = """
        # Made with the visual editor of MatHub. Edit it there: changes here are overwritten.
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'

        function Invoke-MatHubWinget {
            if (-not $env:MATHUB_WINGET) { throw 'winget is not available on this computer.' }
            & $env:MATHUB_WINGET @args --accept-source-agreements --disable-interactivity
            # 0 = done; -1978335189 = nothing to update; -1978335212 = no package found (uninstall of something that is not there)
            if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne -1978335189 -and $LASTEXITCODE -ne -1978335212) { throw "winget ended with $LASTEXITCODE." }
        }

        """;

    // ---- the block editor ----------------------------------------------------------------------------------------------------

    public static CompileResult CompileBlocks(ScriptModel model)
    {
        var errors = new List<string>();
        var sb = new StringBuilder(Prelude);
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

            var code = Step(type, block.Values, step, errors);
            sb.Append("# ").Append(step.ToString(CultureInfo.InvariantCulture)).Append(". ").AppendLine(Localization.Text.T("Blocks.Type." + type.Id));
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
    public static CompileResult CompileFlow(ScriptModel model)
    {
        var errors = new List<string>();
        var sb = new StringBuilder(Prelude);
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

                var code = Step(type, node.Values, number, errors);
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

    // ---- one step, one check ---------------------------------------------------------------------------------------------------

    private static string Step(BlockType type, IReadOnlyDictionary<string, string> values, int step, List<string> errors)
    {
        string V(string name) => values.TryGetValue(name, out var v) ? v.Trim() : string.Empty;
        string Q(string name) => Quote(V(name));
        foreach (var f in type.Fields.Where(f => f.Required && V(f.Name).Length == 0))
        {
            errors.Add(Localization.Text.T("Blocks.Error.Missing", step, Localization.Text.T("Blocks.Field." + f.Name)));
        }

        int Number(string name, int fallback, int min, int max)
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

            errors.Add(Localization.Text.T("Blocks.Error.Number", step, Localization.Text.T("Blocks.Field." + name)));
            return fallback;
        }

        switch (type.Id)
        {
            case "winget-install":
                return $"Invoke-MatHubWinget install --id {Q("id")} --exact --silent --scope machine --accept-package-agreements";
            case "winget-uninstall":
                return $"Invoke-MatHubWinget uninstall --id {Q("id")} --exact --silent";
            case "winget-upgrade":
                return V("id").Length == 0
                    ? "Invoke-MatHubWinget upgrade --all --silent --accept-package-agreements"
                    : $"Invoke-MatHubWinget upgrade --id {Q("id")} --exact --silent --accept-package-agreements";
            case "program":
                var args = V("arguments").Length == 0 ? string.Empty : $" -ArgumentList {Q("arguments")}";
                return $"$p = Start-Process -FilePath {Q("path")}{args} -Wait -PassThru -NoNewWindow\nif ($p.ExitCode -ne 0) {{ throw \"The program ended with $($p.ExitCode).\" }}";
            case "download":
                return $"Invoke-WebRequest -UseBasicParsing -Uri {Q("url")} -OutFile {Q("path")}";
            case "folder":
                return $"New-Item -ItemType Directory -Force -Path {Q("path")} | Out-Null";
            case "copy":
                return $"Copy-Item -Path {Q("source")} -Destination {Q("target")} -Recurse -Force";
            case "delete":
                return $"if (Test-Path -LiteralPath {Q("path")}) {{ Remove-Item -LiteralPath {Q("path")} -Recurse -Force }}";
            case "write-file":
                return $"Set-Content -LiteralPath {Q("path")} -Value {Quote(values.GetValueOrDefault("content") ?? string.Empty)} -Encoding UTF8";
            case "registry":
                var kind = V("type") is "DWord" or "QWord" or "ExpandString" or "MultiString" ? V("type") : "String";
                var value = kind switch
                {
                    "DWord" or "QWord" => long.TryParse(V("value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                        ? n.ToString(CultureInfo.InvariantCulture)
                        : Fail(errors, Localization.Text.T("Blocks.Error.Number", step, Localization.Text.T("Blocks.Field.value"))),
                    "MultiString" => "@(" + string.Join(", ", V("value").Split(';').Select(Quote)) + ")",
                    _ => Q("value"),
                };
                return $"if (-not (Test-Path -Path {Q("path")})) {{ New-Item -Path {Q("path")} -Force | Out-Null }}\nNew-ItemProperty -Path {Q("path")} -Name {Q("name")} -PropertyType {kind} -Value {value} -Force | Out-Null";
            case "registry-delete":
                return V("name").Length == 0
                    ? $"if (Test-Path -Path {Q("path")}) {{ Remove-Item -Path {Q("path")} -Recurse -Force }}"
                    : $"Remove-ItemProperty -Path {Q("path")} -Name {Q("name")} -Force -ErrorAction SilentlyContinue";
            case "service":
                return V("action") switch
                {
                    "stop" => $"Stop-Service -Name {Q("name")} -Force",
                    "restart" => $"Restart-Service -Name {Q("name")} -Force",
                    "automatic" => $"Set-Service -Name {Q("name")} -StartupType Automatic",
                    "manual" => $"Set-Service -Name {Q("name")} -StartupType Manual",
                    "disabled" => $"Set-Service -Name {Q("name")} -StartupType Disabled",
                    _ => $"Start-Service -Name {Q("name")}",
                };
            case "env":
                return $"[Environment]::SetEnvironmentVariable({Q("name")}, {Q("value")}, 'Machine')";
            case "restart":
                var delay = Number("delay", 60, 0, 86400);
                return $"shutdown.exe /r /t {delay} /c 'MatHub restarts this computer.'";
            case "powershell":
                return (values.GetValueOrDefault("code") ?? string.Empty).Trim();
            case "message":
                return $"Write-Output {Q("text")}";
            case "wait":
                return $"Start-Sleep -Seconds {Number("seconds", 10, 0, 86400)}";
            case "exit":
                return $"exit {Number("code", 0, int.MinValue, int.MaxValue)}";
            default:
                errors.Add(Localization.Text.T("Blocks.Error.Unknown", step));
                return string.Empty;
        }
    }

    private static string Condition(BlockCondition condition, int step, List<string> errors)
    {
        var type = Find(condition.Type);
        if (type is null || !type.IsCondition)
        {
            errors.Add(Localization.Text.T("Blocks.Error.Unknown", step));
            return "$false";
        }

        string V(string name) => condition.Values.TryGetValue(name, out var v) ? v.Trim() : string.Empty;
        string Q(string name) => Quote(V(name));
        foreach (var f in type.Fields.Where(f => f.Required && V(f.Name).Length == 0))
        {
            errors.Add(Localization.Text.T("Blocks.Error.Missing", step, Localization.Text.T("Blocks.Field." + f.Name)));
        }

        return type.Id switch
        {
            "file-exists" => $"Test-Path -LiteralPath {Q("path")}",
            "registry-exists" => V("name").Length == 0
                ? $"Test-Path -Path {Q("path")}"
                : $"$null -ne (Get-ItemProperty -Path {Q("path")} -Name {Q("name")} -ErrorAction SilentlyContinue)",
            "service-running" => $"(Get-Service -Name {Q("name")} -ErrorAction SilentlyContinue).Status -eq 'Running'",
            "program-installed" => "@(Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*', 'HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*' -ErrorAction SilentlyContinue | "
                                   + $"Where-Object {{ $_.DisplayName -like ('*' + {Q("name")} + '*') }}).Count -gt 0",
            "os-build" => int.TryParse(V("build"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var build)
                ? $"[Environment]::OSVersion.Version.Build -ge {build}"
                : Fail(errors, Localization.Text.T("Blocks.Error.Number", step, Localization.Text.T("Blocks.Field.build"))),
            "expression" => V("code"),
            _ => "$false",
        };
    }

    private static string Fail(List<string> errors, string message)
    {
        errors.Add(message);
        return "0";
    }

    private static string Indent(string code, int spaces = 4)
    {
        var pad = new string(' ', spaces);
        return string.Join('\n', code.Split('\n').Select(l => l.Length == 0 ? l : pad + l));
    }

    /// <summary>A text inside a double-quoted PowerShell string and a comment: no quote, no $, no backtick, one line.</summary>
    private static string Escape(string text) => new(text.Where(c => c is not ('"' or '$' or '`' or '\n' or '\r')).ToArray());
}
