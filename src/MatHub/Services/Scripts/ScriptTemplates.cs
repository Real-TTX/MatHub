using MatHub.Data;
using static MatHub.Localization.Text;

namespace MatHub.Services.Scripts;

/// <summary>A script to begin with: its editor and its model (blocks or a flow), in the language of the person who takes it.</summary>
public sealed record ScriptTemplate(string Key, ScriptEditor Editor, string Icon, Func<ScriptModel> Model);

/// <summary>
/// The templates of a new script: ready-made blocks and flows with the actions of the catalog (a domain join, a set-up wizard, a restart with a question …).
/// They are a start: the person changes the domain, the OU, the programs. Their texts come from the catalog, in the language of the person.
/// </summary>
public static class ScriptTemplates
{
    public static IReadOnlyList<ScriptTemplate> All { get; } =
    [
        new("domain-join", ScriptEditor.Blocks, "server", DomainJoin),
        new("setup-wizard", ScriptEditor.Flow, "list-checks", SetupWizard),
        new("restart-question", ScriptEditor.Flow, "power", RestartWithQuestion),
        new("standard-software", ScriptEditor.Blocks, "box", StandardSoftware),
        new("maintenance", ScriptEditor.Blocks, "broom", Maintenance),
    ];

    public static ScriptTemplate? Find(string? key) => All.FirstOrDefault(t => t.Key == key);

    private static ScriptBlock B(string type, params (string Key, string Value)[] values) => new() { Type = type, Values = values.ToDictionary(v => v.Key, v => v.Value) };

    private static FlowNode N(string id, string type, double x, double y, params (string Key, string Value)[] values)
        => new() { Id = id, Type = type, X = x, Y = y, Values = values.ToDictionary(v => v.Key, v => v.Value) };

    private static FlowNode If(string id, double x, double y, string check, bool negate, params (string Key, string Value)[] values)
        => new() { Id = id, Type = "if", X = x, Y = y, Condition = new BlockCondition { Type = check, Negate = negate, Values = values.ToDictionary(v => v.Key, v => v.Value) } };

    private static FlowEdge E(string from, string to, string port = "next") => new() { From = from, To = to, Port = port };

    private static ScriptModel DomainJoin() => new()
    {
        Blocks =
        [
            new ScriptBlock { Type = "fail", Values = new() { ["text"] = T("Templates.DomainJoin.NoNetwork") }, When = new BlockCondition { Type = "reachable", Negate = true, Values = new() { ["host"] = "contoso.local" } } },
            B("domain-join", ("domain", "contoso.local"), ("credential", "DOMAIN_JOIN"), ("ou", "OU=Clients,DC=contoso,DC=local"), ("name", string.Empty), ("reboot", "true")),
        ],
    };

    private static ScriptModel StandardSoftware() => new()
    {
        Blocks =
        [
            B("ui-notify", ("title", "IT"), ("text", T("Templates.Software.Notify"))),
            B("winget-install", ("id", "7zip.7zip")),
            B("winget-install", ("id", "Mozilla.Firefox")),
            B("winget-install", ("id", "VideoLAN.VLC")),
            B("winget-install", ("id", "Adobe.Acrobat.Reader.64-bit")),
            B("shortcut", ("path", @"C:\Users\Public\Desktop\Intranet.lnk"), ("target", @"C:\Program Files\Mozilla Firefox\firefox.exe"), ("arguments", "https://intranet.contoso.local")),
        ],
    };

    private static ScriptModel Maintenance() => new()
    {
        Blocks =
        [
            B("cleanup"),
            B("defender", ("scan", "signatures")),
            B("windows-update", ("updates", "security")),
            new ScriptBlock
            {
                Type = "ui-notify", Values = new() { ["title"] = "IT", ["text"] = T("Templates.Maintenance.Restart") },
                When = new BlockCondition { Type = "variable", Values = new() { ["variable"] = "restartNeeded", ["compare"] = "equals", ["value"] = "True" } },
            },
        ],
    };

    /// <summary>A wizard asks for the name, the department and the programs, then the computer is renamed, joined into the OU of its department and set up.</summary>
    private static ScriptModel SetupWizard()
    {
        var fields = System.Text.Json.JsonSerializer.Serialize(new object[]
        {
            new { kind = "page", label = T("Templates.Wizard.Welcome"), @default = T("Templates.Wizard.WelcomeText") },
            new { kind = "info", label = T("Templates.Wizard.Info") },
            new { kind = "page", label = T("Templates.Wizard.Computer") },
            new { kind = "text", name = "computerName", label = T("Templates.Wizard.Name"), @default = "PC-{{serial}}", required = true },
            new { kind = "choice", name = "department", label = T("Templates.Wizard.Department"), options = T("Templates.Wizard.Departments"), required = true },
            new { kind = "page", label = T("Templates.Wizard.Programs") },
            new { kind = "check", name = "office", label = "Microsoft 365 Apps", @default = "true" },
        }, ScriptBlocks.Json);
        return new ScriptModel
        {
            Nodes =
            [
                N("start", "start", 300, 20),
                N("wizard", "ui-form", 300, 120, ("title", T("Templates.Wizard.Title")), ("text", string.Empty), ("fields", fields)),
                If("ok", 300, 240, "variable", false, ("variable", "answer"), ("compare", "equals"), ("value", "ok")),
                N("cancelled", "ui-message", 560, 360, ("title", T("Templates.Wizard.Title")), ("text", T("Templates.Wizard.Cancelled")), ("level", "info")),
                N("progress", "ui-progress", 300, 360, ("title", T("Templates.Wizard.Title")), ("text", T("Templates.Wizard.Working"))),
                N("rename", "rename-computer", 300, 480, ("name", "{{computerName}}")),
                N("join", "domain-join", 300, 600, ("domain", "contoso.local"), ("credential", "DOMAIN_JOIN"), ("ou", "OU={{department}},OU=Clients,DC=contoso,DC=local"), ("reboot", string.Empty)),
                If("office?", 300, 720, "variable", false, ("variable", "office"), ("compare", "equals"), ("value", "true")),
                N("office", "winget-install", 120, 840, ("id", "Microsoft.Office")),
                N("done", "ui-message", 300, 960, ("title", T("Templates.Wizard.Title")), ("text", T("Templates.Wizard.Done")), ("level", "info")),
                N("restart", "restart", 300, 1080, ("delay", "60")),
                N("end", "end", 300, 1200),
                N("end2", "end", 560, 480),
            ],
            Edges =
            [
                E("start", "wizard"), E("wizard", "ok"), E("ok", "progress", "true"), E("ok", "cancelled", "false"), E("cancelled", "end2"),
                E("progress", "rename"), E("rename", "join"), E("join", "office?"), E("office?", "office", "true"), E("office?", "done", "false"),
                E("office", "done"), E("done", "restart"), E("restart", "end"),
            ],
        };
    }

    /// <summary>Somebody at the computer is asked first; nobody there: it restarts at once.</summary>
    private static ScriptModel RestartWithQuestion() => new()
    {
        Nodes =
        [
            N("start", "start", 260, 20),
            If("user?", 260, 120, "user-logged-on", false),
            N("ask", "ui-question", 260, 240, ("title", "IT"), ("text", T("Templates.Restart.Question")), ("variable", "restartNow"), ("timeout", "600")),
            If("yes?", 260, 360, "variable", true, ("variable", "restartNow"), ("compare", "equals"), ("value", "no")),
            N("later", "ui-notify", 520, 480, ("title", "IT"), ("text", T("Templates.Restart.Later"))),
            N("restart", "restart", 40, 480, ("delay", "60")),
            N("end", "end", 260, 600),
        ],
        Edges =
        [
            E("start", "user?"), E("user?", "ask", "true"), E("user?", "restart", "false"), E("ask", "yes?"), E("yes?", "restart", "true"), E("yes?", "later", "false"),
            E("restart", "end"), E("later", "end"),
        ],
    };
}
