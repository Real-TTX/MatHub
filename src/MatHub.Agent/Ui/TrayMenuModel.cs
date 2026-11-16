namespace MatHub.Agent.Ui;

/// <summary>What an entry of the tray menu is (MatDesign TRAY-01), in the order of the menu.</summary>
internal enum TrayRole
{
    Title,
    State,
    Open,
    Info,
    ServiceStart,
    ServiceStop,
    ServiceRestart,
    Quit,
    Separator,
}

/// <summary>One entry: its role, its text, whether it can be chosen, whether it is the default (bold), whether it needs administrator rights (the shield).</summary>
internal sealed record TrayEntry(TrayRole Role, string Text, bool Enabled = true, bool Default = false, bool Shield = false);

/// <summary>
/// The menu of the agent's tray in the order of TRAY-01: the name and the version (disabled), the state in words (disabled), a separator; <c>Open MatHub Kiosk</c>
/// (the default: the kiosk is the window of the tray); <c>Info …</c>; a separator; the actions of the service with the shield (only where the service is installed);
/// a separator; <c>Quit</c>, which ends the tray and never the service (AGENT-04). The agent has no settings, no log folder a user may open and no update to start
/// (it follows its server by itself), so those items are left out; there is no Uninstall (SETUP-04: Installed apps). Plain C#: the tests compile it in.
/// </summary>
internal static class TrayMenuModel
{
    public static IReadOnlyList<TrayEntry> Build(AgentStatus status, bool serviceInstalled)
    {
        var entries = new List<TrayEntry>
        {
            new(TrayRole.Title, Title(status.Version ?? AgentInfo.Version), Enabled: false),
            new(TrayRole.State, StateWord(status.State), Enabled: false),
            new(TrayRole.Separator, string.Empty, Enabled: false),
            new(TrayRole.Open, UiText.T("TrayOpen"), Default: true),
            new(TrayRole.Info, UiText.T("TrayInfo")),
        };

        if (serviceInstalled)
        {
            var runs = status.ServiceRuns;
            entries.Add(new(TrayRole.Separator, string.Empty, Enabled: false));
            entries.Add(new(TrayRole.ServiceStart, UiText.T("ServiceStart"), Enabled: !runs, Shield: true));
            entries.Add(new(TrayRole.ServiceStop, UiText.T("ServiceStop"), Enabled: runs, Shield: true));
            entries.Add(new(TrayRole.ServiceRestart, UiText.T("ServiceRestart"), Enabled: runs, Shield: true));
        }

        entries.Add(new(TrayRole.Separator, string.Empty, Enabled: false));
        entries.Add(new(TrayRole.Quit, UiText.T("TrayQuit")));
        return entries;
    }

    /// <summary>The first line of the menu: the name and the number (SETUP-07).</summary>
    public static string Title(string version) => $"{AgentInfo.DisplayName} {version}";

    public static string StateWord(TrayState state) => UiText.T("State" + state);

    /// <summary>The tooltip of the icon: <c>MatHub Agent: Running</c> (TRAY-02).</summary>
    public static string Tooltip(TrayState state) => UiText.T("Tooltip", AgentInfo.DisplayName, StateWord(state));

    /// <summary>The sentence that explains the state (Info): what the agent does and what is to do.</summary>
    public static string Sentence(AgentStatus status) => status.Condition == AgentCondition.Connected
        ? UiText.T("ConditionConnected", status.LastContact is { } last ? UiText.When(last) : UiText.T("Never"))
        : UiText.T("Condition" + status.Condition);
}
