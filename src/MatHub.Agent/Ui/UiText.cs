using System.Runtime.InteropServices;

namespace MatHub.Agent.Ui;

/// <summary>The texts of the windows of the agent, in German or English (the language of Windows for the person signed in).</summary>
internal static class UiText
{
    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    /// <summary>German when Windows speaks German to the person (LANG_GERMAN = 0x07); MATHUB_AGENT_LANGUAGE=en|de decides for a test.</summary>
    public static bool German { get; set; } = Environment.GetEnvironmentVariable("MATHUB_AGENT_LANGUAGE") is { Length: > 0 } language
        ? language.StartsWith("de", StringComparison.OrdinalIgnoreCase)
        : WindowsSpeaksGerman();

    private static bool WindowsSpeaksGerman()
    {
        try
        {
            return (GetUserDefaultUILanguage() & 0x3FF) == 0x07;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // not Windows (the tests on Linux): English
            return false;
        }
    }

    /// <summary>The name of the computer the kiosk names (MATHUB_AGENT_COMPUTER: another one for the pictures of the documentation).</summary>
    public static string Computer => Environment.GetEnvironmentVariable("MATHUB_AGENT_COMPUTER") is { Length: > 0 } name ? name : Environment.MachineName;

    private static readonly Dictionary<string, (string En, string De)> Texts = new(StringComparer.Ordinal)
    {
        ["KioskTitle"] = ("MatHub Kiosk", "MatHub Kiosk"),
        ["KioskSub"] = ("Programs and help for {0}", "Programme und Hilfe für {0}"),
        ["Search"] = ("Search", "Suchen"),
        ["Refresh"] = ("Refresh", "Aktualisieren"),
        ["All"] = ("All", "Alle"),
        ["Start"] = ("Start", "Starten"),
        ["Again"] = ("Start again", "Erneut starten"),
        ["Queued"] = ("Waiting …", "Wartet …"),
        ["Running"] = ("Running …", "Läuft …"),
        ["Step"] = ("Step {0} of {1}", "Schritt {0} von {1}"),
        ["Done"] = ("Done", "Fertig"),
        ["Failed"] = ("Did not work", "Hat nicht geklappt"),
        ["Cancelled"] = ("Cancelled", "Abgebrochen"),
        ["Last"] = ("Last time {0}", "Zuletzt {0}"),
        ["Updated"] = ("Up to date · {0}", "Aktuell · {0}"),
        ["Loading"] = ("Loading the offers …", "Lade die Angebote …"),
        ["Offline"] = ("MatHub cannot be reached just now. Try again in a moment.", "MatHub ist gerade nicht erreichbar. Versuche es gleich noch einmal."),
        ["NoAgent"] = ("The MatHub agent does not run on this computer.", "Der MatHub-Agent läuft auf diesem Computer nicht."),
        ["Empty"] = ("There are no offers for this computer just now.", "Für diesen Computer gibt es gerade keine Angebote."),
        ["NoMatch"] = ("Nothing fits your search.", "Nichts passt zu deiner Suche."),
        ["Gone"] = ("This offer is not there any more.", "Dieses Angebot gibt es nicht mehr."),
        // the tray (MatDesign TRAY-01, TRAY-02)
        ["TrayOpen"] = ("Open MatHub Kiosk", "MatHub Kiosk öffnen"),
        ["TrayInfo"] = ("Info …", "Info …"),
        ["ServiceStart"] = ("Start service …", "Dienst starten …"),
        ["ServiceStop"] = ("Stop service …", "Dienst beenden …"),
        ["ServiceRestart"] = ("Restart service …", "Dienst neu starten …"),
        ["TrayQuit"] = ("Quit", "Beenden"),
        ["Tooltip"] = ("{0}: {1}", "{0}: {1}"),
        ["StateRunning"] = ("Running", "Läuft"),
        ["StateWaiting"] = ("Waiting", "Wartet"),
        ["StateStopped"] = ("Stopped", "Gestoppt"),
        ["StateNotSetUp"] = ("Not set up", "Nicht eingerichtet"),
        ["ConditionConnecting"] = ("The agent is connecting to MatHub …", "Der Agent verbindet sich mit MatHub …"),
        ["ConditionConnected"] = ("Connected to MatHub, last contact {0}.", "Mit MatHub verbunden, zuletzt {0}."),
        ["ConditionPending"] = ("This computer waits for an approval in MatHub.", "Dieser Computer wartet auf die Freigabe in MatHub."),
        ["ConditionUnreachable"] = ("MatHub cannot be reached just now; the agent tries again by itself.", "MatHub ist gerade nicht erreichbar; der Agent versucht es von selbst wieder."),
        ["ConditionRejected"] = ("MatHub does not accept this computer (removed or switched off).", "MatHub nimmt diesen Computer nicht an (entfernt oder abgeschaltet)."),
        ["ConditionServiceStopped"] = ("The service of the agent does not run.", "Der Dienst des Agenten läuft nicht."),
        ["ConditionNotInstalled"] = ("The agent is not installed on this computer.", "Der Agent ist auf diesem Computer nicht installiert."),
        ["ServiceFailed"] = ("The service could not be changed: {0}", "Der Dienst konnte nicht geändert werden: {0}"),

        // Info (TRAY-05) and the uninstall (SETUP-04)
        ["Info"] = ("Info", "Info"),
        ["FactComputer"] = ("Computer", "Computer"),
        ["FactServer"] = ("Server", "Server"),
        ["FactLastContact"] = ("Last contact", "Letzter Kontakt"),
        ["FactStarted"] = ("Running since", "Läuft seit"),
        ["FactVersion"] = ("Version", "Version"),
        ["FactData"] = ("Data folder", "Datenordner"),
        ["Never"] = ("not yet", "noch nicht"),
        ["OpenKiosk"] = ("Open kiosk", "Kiosk öffnen"),
        ["Close"] = ("Close", "Schließen"),
        ["Uninstall"] = ("Uninstall", "Deinstallation"),
        ["UninstallTitle"] = ("Remove MatHub Agent from this computer?", "MatHub Agent von diesem Computer entfernen?"),
        ["UninstallWhat"] = (
            "The service, the program in {0}, the symbol beside the clock and the kiosk in the start menu are removed. The computer stays in MatHub until it is removed there.",
            "Der Dienst, das Programm in {0}, das Symbol neben der Uhr und der Kiosk im Startmenü werden entfernt. Der Computer bleibt in MatHub, bis er dort entfernt wird."),
        ["UninstallData"] = ("Also delete my data", "Auch meine Daten löschen"),
        ["UninstallKeeps"] = ("Without the tick the settings stay in {0}.", "Ohne Haken bleiben die Einstellungen in {0}."),
        ["UninstallButton"] = ("Uninstall", "Deinstallieren"),
        ["Removing"] = ("Removing …", "Wird entfernt …"),
        ["Removed"] = ("MatHub Agent is removed.", "MatHub Agent ist entfernt."),
        ["RemovedData"] = ("Its data is deleted too.", "Seine Daten sind auch gelöscht."),
        ["RemovedKept"] = ("Its settings are kept in {0}.", "Seine Einstellungen bleiben in {0}."),
        ["RemoveFailed"] = ("The uninstall did not work (exit code {0}).", "Die Deinstallation hat nicht geklappt (Exit-Code {0})."),
        ["Started"] = ("{0} has started.", "{0} wurde gestartet."),
        ["Finished"] = ("{0} is done.", "{0} ist fertig."),
        ["FailedNotice"] = ("{0} did not work. Please ask your IT.", "{0} hat nicht geklappt. Bitte wende dich an die IT."),
        ["Ok"] = ("OK", "OK"),
        ["Cancel"] = ("Cancel", "Abbrechen"),
        ["Yes"] = ("Yes", "Ja"),
        ["No"] = ("No", "Nein"),
        ["Back"] = ("Back", "Zurück"),
        ["Next"] = ("Next", "Weiter"),
        ["Finish"] = ("Finish", "Fertig"),
        ["Required"] = ("Please fill in: {0}", "Bitte ausfüllen: {0}"),
        ["PageOf"] = ("Step {0} of {1}", "Schritt {0} von {1}"),
        ["Closes"] = ("Closes in {0} s", "Schließt in {0} s"),
        ["Wait"] = ("Please wait …", "Bitte warten …"),
        ["Today"] = ("today {0}", "heute {0}"),
    };

    public static string T(string key, params object[] args)
    {
        var text = Texts.TryGetValue(key, out var pair) ? (German ? pair.De : pair.En) : key;
        return args.Length == 0 ? text : string.Format(System.Globalization.CultureInfo.InvariantCulture, text, args);
    }

    /// <summary>A moment as a person reads it: "today 10:42" or "12.11. 10:42".</summary>
    public static string When(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.Date == DateTime.Today ? T("Today", local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)) : local.ToString(German ? "dd.MM. HH:mm" : "MM/dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }
}
