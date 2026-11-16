using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MatDesktop;
using MatHub.Agent;
using MatHub.Agent.Ui;

namespace MatHub.Tests;

/// <summary>
/// The agent against the desktop rules of MatDesign: its names (AGENT-01), its version (SETUP-07), its entry in Installed apps (SETUP-04), the states and the menu
/// of its tray (TRAY-01, TRAY-02), the snapshot of its status channel without a secret (AGENT-03), and the copy of MatDesktop's tokens it takes its colours from
/// (DESK-02, AGENT-05).
/// </summary>
public sealed class AgentDesktopTests
{
    [Theory]
    [InlineData("0.1.42-20261112", "release")]
    [InlineData("nightly-42-20261112", "nightly")]
    [InlineData("local-20261112", "local")]
    public void The_channel_is_read_from_the_form_of_the_version(string version, string channel)
    {
        Assert.Equal(channel, AgentInfo.ChannelOf(version));
        Assert.Equal($"Version {version} · {channel}", AgentInfo.FormatVersion(version, channel));
    }

    [Fact]
    public void The_names_follow_AGENT_01()
    {
        Assert.Equal("MatHub Agent", AgentInfo.ServiceName);
        Assert.Equal("MatHub Agent", AgentInfo.DisplayName);
        Assert.Equal("MatHub Agent Tray", AgentInfo.TrayRunValue);
        Assert.StartsWith("mathub-agent-status", AgentInfo.StatusPipeName, StringComparison.Ordinal);
        Assert.EndsWith(@"\MatHub Agent", InstalledApp.KeyPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Installed_apps_gets_the_name_publisher_version_icon_and_the_uninstall()
    {
        const string exe = @"C:\Program Files\MatHub Agent\mathub-agent.exe";
        var values = InstalledApp.Values(exe, @"C:\Program Files\MatHub Agent", "0.1.42-20261112", "Matthias Schmoldt", 80L * 1024 * 1024).ToDictionary(v => v.Name, v => v.Value);

        Assert.Equal("MatHub Agent", values["DisplayName"]);
        Assert.Equal("Matthias Schmoldt", values["Publisher"]);
        Assert.Equal("0.1.42-20261112", values["DisplayVersion"]);
        Assert.Equal(exe + ",0", values["DisplayIcon"]);
        Assert.Equal($"\"{exe}\" uninstall", values["UninstallString"]);

        // quiet: no window and the data kept (SETUP-04, SETUP-05): never --delete-data
        Assert.Equal($"\"{exe}\" uninstall --quiet", values["QuietUninstallString"]);
        Assert.DoesNotContain(values.Values.OfType<string>(), v => v.Contains("--delete-data", StringComparison.Ordinal));
        Assert.Equal(80 * 1024, values["EstimatedSize"]);
        Assert.IsType<int>(values["NoModify"]);
    }

    [Theory]
    [InlineData(AgentCondition.Connected, TrayState.Running)]
    [InlineData(AgentCondition.Connecting, TrayState.Waiting)]
    [InlineData(AgentCondition.Pending, TrayState.Waiting)]
    [InlineData(AgentCondition.Unreachable, TrayState.Waiting)]
    [InlineData(AgentCondition.Rejected, TrayState.Stopped)]
    [InlineData(AgentCondition.ServiceStopped, TrayState.Stopped)]
    [InlineData(AgentCondition.NotInstalled, TrayState.NotSetUp)]
    public void Every_condition_is_one_of_the_four_states(AgentCondition condition, TrayState state) => Assert.Equal(state, AgentStatus.StateOf(condition));

    [Fact]
    public void The_tray_menu_has_the_order_of_TRAY_01()
    {
        UiText.German = false;
        var status = new AgentStatus { Condition = AgentCondition.Connected, Version = "1.4.2", LastContact = DateTime.UtcNow };
        var menu = TrayMenuModel.Build(status, serviceInstalled: true);

        Assert.Equal(
            [TrayRole.Title, TrayRole.State, TrayRole.Separator, TrayRole.Open, TrayRole.Info, TrayRole.Separator,
             TrayRole.ServiceStart, TrayRole.ServiceStop, TrayRole.ServiceRestart, TrayRole.Separator, TrayRole.Quit],
            menu.Select(e => e.Role));
        Assert.Equal("MatHub Agent 1.4.2", menu[0].Text);
        Assert.False(menu[0].Enabled);
        Assert.Equal("Running", menu[1].Text);
        Assert.False(menu[1].Enabled);
        Assert.Equal(TrayRole.Open, Assert.Single(menu, e => e.Default).Role);
        Assert.Equal("Quit", menu[^1].Text);
        Assert.All(menu.Where(e => e.Role is TrayRole.ServiceStart or TrayRole.ServiceStop or TrayRole.ServiceRestart), e => Assert.True(e.Shield));
        Assert.DoesNotContain(menu, e => e.Text.Contains("Uninstall", StringComparison.OrdinalIgnoreCase));

        // a service that runs can be stopped and restarted, not started
        Assert.False(menu.Single(e => e.Role == TrayRole.ServiceStart).Enabled);
        Assert.True(menu.Single(e => e.Role == TrayRole.ServiceRestart).Enabled);
    }

    [Fact]
    public void Without_the_service_the_menu_leaves_its_actions_out_and_keeps_the_order()
    {
        UiText.German = true;
        try
        {
            var menu = TrayMenuModel.Build(new AgentStatus { Condition = AgentCondition.NotInstalled, Version = "1.4.2" }, serviceInstalled: false);
            Assert.Equal([TrayRole.Title, TrayRole.State, TrayRole.Separator, TrayRole.Open, TrayRole.Info, TrayRole.Separator, TrayRole.Quit], menu.Select(e => e.Role));
            Assert.Equal("Nicht eingerichtet", menu[1].Text);
            Assert.Equal("MatHub Kiosk öffnen", menu[3].Text);
            Assert.Equal("Beenden", menu[^1].Text);

            // a stopped service can be started
            var stopped = TrayMenuModel.Build(new AgentStatus { Condition = AgentCondition.ServiceStopped, Version = "1.4.2" }, serviceInstalled: true);
            Assert.Equal("Gestoppt", stopped[1].Text);
            Assert.True(stopped.Single(e => e.Role == TrayRole.ServiceStart).Enabled);
            Assert.False(stopped.Single(e => e.Role == TrayRole.ServiceStop).Enabled);
        }
        finally
        {
            UiText.German = false;
        }
    }

    [Fact]
    public void Every_state_and_condition_has_its_words_in_both_languages()
    {
        foreach (var german in new[] { false, true })
        {
            UiText.German = german;
            foreach (var state in Enum.GetValues<TrayState>())
            {
                Assert.NotEqual("State" + state, TrayMenuModel.StateWord(state));
                Assert.Equal($"MatHub Agent: {TrayMenuModel.StateWord(state)}", TrayMenuModel.Tooltip(state));
            }

            foreach (var condition in Enum.GetValues<AgentCondition>())
            {
                var sentence = TrayMenuModel.Sentence(new AgentStatus { Condition = condition });
                Assert.DoesNotContain("Condition", sentence, StringComparison.Ordinal);
                Assert.DoesNotContain("{0}", sentence, StringComparison.Ordinal);
                Assert.True(sentence.EndsWith('.') || sentence.EndsWith('…'), sentence);
            }
        }

        UiText.German = false;
    }

    [Fact]
    public void The_status_channel_carries_no_secret()
    {
        var json = JsonSerializer.Serialize(new AgentStatusBoard("https://hub.example.com").Snapshot(), AgentStatus.Json);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(["condition", "server", "lastContact", "started", "version"], doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Connecting", doc.RootElement.GetProperty("condition").GetString());

        var back = JsonSerializer.Deserialize<AgentStatus>(json, AgentStatus.Json)!;
        Assert.Equal(TrayState.Waiting, back.State);
    }

    [Fact]
    public void The_board_follows_the_calls_of_the_service()
    {
        var board = new AgentStatusBoard("https://hub.example.com");
        board.Contact(pending: true);
        Assert.Equal(AgentCondition.Pending, board.Snapshot().Condition);
        board.Contact(pending: false);
        Assert.Equal(TrayState.Running, board.Snapshot().State);
        Assert.NotNull(board.Snapshot().LastContact);
        board.Set(AgentCondition.Rejected);
        Assert.Equal(TrayState.Stopped, board.Snapshot().State);
    }

    [Fact]
    public void The_colours_come_from_the_tokens_for_the_accent_of_MatHub()
    {
        var light = MatPalette.For("#b45309", dark: false);
        Assert.Equal("#b45309", light.Accent.Hex);
        Assert.True(MatColor.Contrast(light.OnAccent, light.Accent) >= 4.5);
        Assert.True(MatColor.Contrast(light.AccentText, light.Surface) >= 4.5);
        Assert.True(MatColor.Contrast(light.TextMuted.Over(light.Surface), light.Surface) >= 4.5);
    }

    [Fact]
    public void The_copy_of_MatDesktop_is_unchanged()
    {
        var folder = Path.Combine(Root(), "src", "MatHub.Agent", "MatDesktop");
        var manifest = File.ReadAllLines(Path.Combine(folder, "matdesktop.version"));
        Assert.StartsWith("MatDesktop ", manifest[0], StringComparison.Ordinal);
        var files = manifest.Skip(1).Where(l => l.Length > 0).ToList();
        Assert.NotEmpty(files);
        foreach (var line in files)
        {
            var hash = line[..64];
            var file = line[66..];
            var text = File.ReadAllText(Path.Combine(folder, file)).Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.True(hash == Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
                $"MatDesktop/{file} was changed by hand: copy it again with node ../MatDesign/tools/matdesktop-copy.js src/MatHub.Agent --tokens-only");
        }
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MatHub.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("MatHub.slnx was not found.");
    }
}
