using System.Text.Json;
using MatHub.Data;
using MatHub.Services;
using MatHub.Services.Access;
using MatHub.Services.Agent;
using MatHub.Services.Deployments;
using MatPlatform;

namespace MatHub.Platform;

/// <summary>
/// MatHub as an app of the platform contract (MatDesign docs/PLATFORM.md, contract 2) on the shared package MatPlatform (the folder <c>MatPlatform/</c>, a copy):
/// how a consumer such as Mattiverse shows MatHub (<see cref="Describe"/>: tiles, settings, buttons), what it consumes (the sign-in through MatIdentity and the
/// mail through MatSMTP, both set by their variables), what it adds to a shell (the launcher MatHub, two widgets for administrators), its roles and permissions
/// (MatIdentity can give them), its values and what its buttons do. The settings are those of the admin page System, kept in the same place
/// (<see cref="InstanceService"/>). The values come from the services (no DbContext here: this is the web layer).
/// </summary>
public sealed class MatHubPlatform(InstanceService instance, AgentFiles agent, IServiceScopeFactory scopes, MatPlatformSettings settings, ILogger<MatHubPlatform> log)
    : IMatPlatformState, IMatPlatformConfiguration, IMatPlatformActions, IMatPlatformHealth
{
    /// <summary>The id of MatHub in the platform (and the default client id at MatIdentity).</summary>
    public const string AppId = "mathub";

    /// <summary>The accent of MatHub (amber, white text 5.0:1; see <c>wwwroot/css/app.css</c>).</summary>
    public const string Accent = "#b45309";

    /// <summary>The MatApiDefinition of MatHub (<c>GET /api</c>) without <c>MAT_PUBLIC_URL</c>.</summary>
    public static void Describe(MatPlatformBuilder o) => Describe(o, null);

    /// <summary>
    /// The MatApiDefinition of MatHub (<c>GET /api</c>); <c>auth</c> and <c>endpoints</c> are set by MatPlatform. <paramref name="publicUrl"/> is the value of
    /// <c>MAT_PUBLIC_URL</c>: while it is set, the field <c>publicUrl</c> is fixed.
    /// </summary>
    public static void Describe(MatPlatformBuilder o, string? publicUrl)
    {
        var T = o.T;

        o.App(AppId, "MatHub", T("Client-Management: Inventar, Softwareverteilung und Updates, Skripte und Rollout über das Netzwerk",
            "Client management: inventory, software deployment and updates, scripts and rollout over the network"), AppInfo.Version, "/img/logo/mathub-mark.svg", Accent, "/");

        o.Consumes(MatCapabilities.IdentityOidc, optional: true);
        o.Consumes(MatCapabilities.MailSmtp, optional: true);

        // what MatHub adds to the home screen of a shell (Mattiverse)
        o.Launcher("hub", "MatHub", "/img/logo/mathub-mark.svg", "/");
        o.QuickAction("hub", T("Clients", "Clients"), "monitor", url: "/clients");
        o.QuickAction("hub", T("Updates", "Updates"), "refresh", url: "/updates");
        o.QuickAction("hub", T("Aufträge", "Jobs"), "activity", url: "/jobs");
        o.QuickAction("hub", T("Rollout", "Rollout"), "server", url: "/rollout");
        o.Widget(new MatWidget("clients", T("Clients", "Clients"), MatWidgetKinds.Tiles, "medium")
        {
            Items = ["clients", "online", "updates"],
            Icon = "monitor",
            Url = "/clients",
            Audience = MatAudience.Admin,
        });
        o.Widget(new MatWidget("failed", T("Fehlgeschlagen (24 h)", "Failed (24 h)"), MatWidgetKinds.Stat)
        {
            Items = ["failedRuns"],
            Icon = "alert",
            Url = "/jobs?state=failed",
            Audience = MatAudience.Admin,
        });

        o.Page("overview", T("Übersicht", "Overview"), "dashboard",
            MatSection.Tiles("clients", "online", "pending", "updates", "openRuns", "failedRuns", "scripts", "packages", "rollouts", "users", "registration", "lastActivity"),
            MatSection.Actions("openApp", "openClients", "openUpdates", "openJobs", "openUsers", "openConnections", "openActivity", "openSystem", "plan", "cleanUp"));
        o.Page("settings", T("Einstellungen", "Settings"), "settings",
            MatSection.Form(T("Allgemein", "General"), "registrationOpen", "publicUrl"),
            MatSection.Form(T("Agenten", "Agents"), "agentPollSeconds", "inventoryHours", "jobDays"),
            MatSection.Form(T("Netzwerk-Boot", "Network boot"), "bootUrl"));

        o.Tile("clients", T("Clients", "Clients"), icon: "monitor");
        o.Tile("online", T("Online", "Online"), icon: "check-circle");
        o.Tile("pending", T("Warten auf Freigabe", "Waiting for approval"), icon: "clock");
        o.Tile("updates", T("Clients mit Updates", "Clients with updates"), icon: "refresh");
        o.Tile("openRuns", T("Offene Ausführungen", "Open runs"), icon: "play");
        o.Tile("failedRuns", T("Fehlgeschlagen (24 h)", "Failed (24 h)"), icon: "alert");
        o.Tile("scripts", T("Skripte", "Scripts"), icon: "terminal");
        o.Tile("packages", T("Pakete", "Packages"), icon: "box");
        o.Tile("rollouts", T("Offene Rollouts", "Open rollouts"), icon: "server");
        o.Tile("users", T("Aktive Benutzer", "Active users"), icon: "users");
        o.Tile("registration", T("Registrierung", "Sign-up"), "status", icon: "user");
        o.Tile("lastActivity", T("Letzte Aktivität", "Last activity"), "time", icon: "activity");

        o.Field(new MatField("registrationOpen", T("Registrierung offen", "Open sign-up"), "bool")
        {
            Hint = T("Aus: nur Administratoren legen Konten an.", "Off: only administrators create accounts."),
        });
        var address = new MatField("publicUrl", T("Öffentliche Adresse", "Public address"), "url")
        {
            Max = 300,
            Placeholder = "https://hub.example.com",
            Hint = T("Für E-Mails und die Installation des Agenten; leer: die Adresse des Aufrufs.", "For mails and the installation of the agent; empty: the address of the request."),
        };
        o.Field(publicUrl is null ? address : address with
        {
            Fixed = true,
            Hint = T("Festgelegt durch MAT_PUBLIC_URL (Mattiverse); hier lässt sie sich nicht ändern.", "Set by MAT_PUBLIC_URL (Mattiverse); it cannot be changed here."),
        });
        o.Field(new MatField("agentPollSeconds", T("Agenten fragen alle (Sekunden)", "Agents call every (seconds)"), "number") { Min = 10, Max = 3600 });
        o.Field(new MatField("inventoryHours", T("Inventar alle (Stunden)", "Inventory every (hours)"), "number") { Min = 1, Max = 168 });
        o.Field(new MatField("jobDays", T("Aufträge behalten (Tage)", "Keep jobs (days)"), "number") { Min = 1, Max = 3650 });
        o.Field(new MatField("bootUrl", T("Adresse für den Netzwerk-Boot", "Address for the network boot"), "url")
        {
            Max = 300,
            Placeholder = "http://10.0.0.5:4910",
            Hint = T("Leer: die Adresse des TFTP-Servers mit dem Port von MatHub.", "Empty: the address of the TFTP server with the port of MatHub."),
        });

        o.LinkAction("openApp", T("Öffnen", "Open"), "external", "/");
        o.LinkAction("openClients", T("Clients", "Clients"), "monitor", "/clients");
        o.LinkAction("openUpdates", T("Updates", "Updates"), "refresh", "/updates");
        o.LinkAction("openJobs", T("Aufträge", "Jobs"), "activity", "/jobs");
        o.LinkAction("openUsers", T("Benutzer", "Users"), "users", "/Admin/Users");
        o.LinkAction("openConnections", T("Verbindungen", "Connections"), "plug", "/Admin/Connections");
        o.LinkAction("openActivity", T("Aktivität", "Activity"), "activity", "/Admin/Activity");
        o.LinkAction("openSystem", T("System", "System"), "settings", "/Admin/System");
        o.PostAction("plan", T("Zuweisungen jetzt prüfen", "Check assignments now"), "refresh");
        o.PostAction(
            "cleanUp",
            T("Aufräumen", "Clean up"),
            "broom",
            T("Beendete Sitzungen, alte Aufträge und abgeschlossene Rollouts jetzt entfernen?", "Remove ended sessions, old jobs and finished rollouts now?"));

        o.Role("admin", T("Administrator", "Administrator"));
        o.Role(RoleService.Technician, T("Techniker", "Technician"));
        o.Role(RoleService.Operator, T("Operator", "Operator"));
        o.Role(RoleService.Viewer, T("Betrachter", "Viewer"));

        // what MatIdentity can grant on top of a role (2.4.0); the roles bring theirs (RoleService.SeedAsync)
        string[] all = [RoleService.Technician];
        string[] operate = [RoleService.Technician, RoleService.Operator];
        string[] view = [RoleService.Technician, RoleService.Operator, RoleService.Viewer];
        o.Permission(Permission.ClientsView, T("Clients ansehen", "View clients"), null, view);
        o.Permission(Permission.ClientsManage, T("Clients verwalten", "Manage clients"), null, all);
        o.Permission(Permission.ClientsEnroll, T("Clients aufnehmen", "Enroll clients"), T("Schlüssel für die Aufnahme, Freigaben.", "Enrollment keys, approvals."), all);
        o.Permission(Permission.GroupsManage, T("Gruppen verwalten", "Manage groups"), null, all);
        o.Permission(Permission.ScriptsView, T("Skripte ansehen", "View scripts"), null, view);
        o.Permission(Permission.ScriptsEdit, T("Skripte bearbeiten", "Edit scripts"), null, all);
        o.Permission(Permission.ScriptsRun, T("Skripte ausführen", "Run scripts"), null, operate);
        o.Permission(Permission.SecretsManage, T("Geheimnisse verwalten", "Manage secrets"), T("Kennwörter für Skripte, z. B. für den Domänenbeitritt.", "Passwords for scripts, e.g. for a domain join."), all);
        o.Permission(Permission.PackagesView, T("Pakete ansehen", "View packages"), null, view);
        o.Permission(Permission.PackagesEdit, T("Pakete bearbeiten", "Edit packages"), null, all);
        o.Permission(Permission.PackagesDeploy, T("Software verteilen", "Deploy software"), T("Zuweisungen und Updates.", "Assignments and updates."), operate);
        o.Permission(Permission.JobsView, T("Aufträge ansehen", "View jobs"), null, view);
        o.Permission(Permission.JobsManage, T("Aufträge abbrechen", "Cancel jobs"), null, operate);
        o.Permission(Permission.RolloutView, T("Rollout ansehen", "View rollout"), null, view);
        o.Permission(Permission.RolloutManage, T("Rollout verwalten", "Manage rollout"), null, all);
        o.Permission(Permission.KioskManage, T("Kiosk verwalten", "Manage the kiosk"), T("Was die Benutzer an ihren Computern selbst starten.", "What people start at their computers themselves."), all);
    }

    public async Task<IReadOnlyList<MatHealthCheck>> CheckHealthAsync(MatPlatformCall call, CancellationToken ct)
    {
        if (!await instance.CanConnectAsync(ct))
        {
            return [new MatHealthCheck("database", "down", call.T("Die Datenbank ist nicht erreichbar.", "The database cannot be reached."))];
        }

        var checks = new List<MatHealthCheck> { new("database", "ok") };
        checks.Add(instance.CanWriteFiles() ? new MatHealthCheck("files", "ok") : new MatHealthCheck("files", "down", call.T("Der Datenordner ist nicht beschreibbar.", "The data folder cannot be written.")));
        checks.Add(agent.Available
            ? new MatHealthCheck("agent", "ok")
            : new MatHealthCheck("agent", "degraded", call.T("Kein Agent zum Verteilen (mathub-agent.exe fehlt).", "No agent to hand out (mathub-agent.exe is missing).")));
        if (await instance.AnyUserAsync() && (await instance.SnapshotAsync(ct)).Admins == 0)
        {
            checks.Add(new MatHealthCheck("administrators", "degraded", call.T("Kein aktiver Administrator.", "No active administrator.")));
        }

        return checks;
    }

    public async Task<IReadOnlyDictionary<string, MatStateValue>> GetStateAsync(MatPlatformCall call, CancellationToken ct)
    {
        var s = await instance.SnapshotAsync(ct);
        var (open, _) = await instance.SettingsAsync();
        return new Dictionary<string, MatStateValue>
        {
            ["clients"] = new(s.Clients),
            ["online"] = new(s.Online),
            ["pending"] = s.Pending > 0 ? new MatStateValue(s.Pending, "warn") : new MatStateValue(s.Pending),
            ["updates"] = new(s.ClientsWithUpdates),
            ["openRuns"] = new(s.OpenRuns),
            ["failedRuns"] = s.FailedRuns24h > 0 ? new MatStateValue(s.FailedRuns24h, "error") : new MatStateValue(s.FailedRuns24h),
            ["scripts"] = new(s.Scripts),
            ["packages"] = new(s.Packages),
            ["rollouts"] = new(s.RolloutsWaiting),
            ["users"] = new(s.Users),
            ["registration"] = open
                ? new MatStateValue("info", Text: call.T("Offen", "Open"))
                : new MatStateValue("ok", Text: call.T("Geschlossen", "Closed")),
            ["lastActivity"] = new(s.LastActivityUtc is { } last ? new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc)) : null),
        };
    }

    public async Task<IReadOnlyDictionary<string, object?>> GetConfigurationAsync(MatPlatformCall call, CancellationToken ct)
    {
        var st = await instance.HubSettingsAsync();
        return new Dictionary<string, object?>
        {
            ["registrationOpen"] = st.RegistrationOpen,
            ["publicUrl"] = st.PublicUrl ?? string.Empty,
            ["agentPollSeconds"] = st.AgentPollSeconds,
            ["inventoryHours"] = st.InventoryHours,
            ["jobDays"] = st.JobDays,
            ["bootUrl"] = st.BootUrl ?? string.Empty,
        };
    }

    /// <summary>The values are checked against the definition already (MatPlatform, <c>CheckValues</c>); the business layer checks them once more like on the admin page.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ConfigureAsync(IReadOnlyDictionary<string, JsonElement> values, MatPlatformCall call, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        var change = new HubSettingsChange();
        foreach (var (key, value) in values)
        {
            switch (key)
            {
                case "registrationOpen" when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    change = change with { RegistrationOpen = value.GetBoolean() };
                    break;
                case "publicUrl" when settings.AppPublicUrl is not null:
                    errors[key] = call.T("Festgelegt durch MAT_PUBLIC_URL.", "Set by MAT_PUBLIC_URL.");
                    break;
                case "publicUrl" when value.ValueKind is JsonValueKind.String or JsonValueKind.Null:
                    change = change with { PublicUrl = value.ValueKind == JsonValueKind.Null ? string.Empty : value.GetString() };
                    break;
                case "bootUrl" when value.ValueKind is JsonValueKind.String or JsonValueKind.Null:
                    change = change with { BootUrl = value.ValueKind == JsonValueKind.Null ? string.Empty : value.GetString() };
                    break;
                case "agentPollSeconds" when value.TryGetInt32(out var poll):
                    change = change with { AgentPollSeconds = poll };
                    break;
                case "inventoryHours" when value.TryGetInt32(out var hours):
                    change = change with { InventoryHours = hours };
                    break;
                case "jobDays" when value.TryGetInt32(out var days):
                    change = change with { JobDays = days };
                    break;
                default:
                    errors[key] = call.T("Dieser Wert passt nicht.", "This value does not fit.");
                    break;
            }
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        if (await instance.SaveSettingsAsync(change) is { } problem)
        {
            errors[char.ToLowerInvariant(problem.Field[0]) + problem.Field[1..]] = problem.Message;
            return errors;
        }

        log.LogInformation("Settings changed through the platform API by {Caller}: {Keys}", call.Caller, string.Join(", ", values.Keys));
        return errors;
    }

    public async Task<MatActionResult?> RunActionAsync(string id, MatPlatformCall call, CancellationToken ct)
    {
        switch (id)
        {
            case "plan":
                using (var scope = scopes.CreateScope())
                {
                    var result = await Planner.RunOnceAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), ct);
                    log.LogInformation("Assignments checked through the platform API by {Caller}: {Jobs} jobs", call.Caller, result.Jobs);
                    return new MatActionResult(true, call.T($"{result.Jobs} Aufträge mit {result.Runs} Ausführungen angelegt.", $"{result.Jobs} jobs with {result.Runs} runs made."));
                }

            case "cleanUp":
                var cleaned = await instance.CleanUpAsync(ct);
                log.LogInformation("Clean-up through the platform API by {Caller}: {Sessions} sessions, {Jobs} jobs", call.Caller, cleaned.Sessions, cleaned.Jobs);
                return new MatActionResult(true, call.T(
                    $"Aufgeräumt: {cleaned.Sessions} Sitzungen, {cleaned.Jobs} Aufträge, {cleaned.Rollouts} Rollouts.",
                    $"Cleaned up: {cleaned.Sessions} sessions, {cleaned.Jobs} jobs, {cleaned.Rollouts} rollouts."));
            default:
                return null;
        }
    }
}
