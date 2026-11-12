using MatHub.Agent;
using MatHub.Data;
using MatHub.Services.Agent;
using MatHub.Services.Clients;

namespace MatHub.Tests;

/// <summary>What the agent reads (the tables of winget, in any language) and what the server makes of what it reports.</summary>
public sealed class AgentTests
{
    [Fact]
    public void The_upgrade_table_of_winget_is_read_by_its_header()
    {
        const string output = "   - \r   \\ \r\n" +
            "Name                               Id                          Version      Available    Source\n" +
            "-----------------------------------------------------------------------------------------------\n" +
            "Mozilla Firefox (x64 de)           Mozilla.Firefox             128.0        129.0.1      winget\n" +
            "7-Zip 23.01 (x64)                  7zip.7zip                   23.01        24.08        winget\n" +
            "Microsoft Visual Studio Commun…    Microsoft.VisualStudio.20…  17.10.1      17.11.0      winget\n" +
            "3 upgrades available.\n";
        var rows = Winget.ParseTable(output);
        Assert.Equal(3, rows.Count);
        Assert.Equal(["Mozilla Firefox (x64 de)", "Mozilla.Firefox", "128.0", "129.0.1", "winget"], rows[0]);
        Assert.Equal("7zip.7zip", rows[1][1]);
    }

    [Fact]
    public void A_german_table_is_read_the_same_way()
    {
        const string output =
            "Name                     ID                    Version  Verfügbar  Quelle\n" +
            "-------------------------------------------------------------------------\n" +
            "Notepad++ (64-bit x64)   Notepad++.Notepad++   8.6.4    8.6.9      winget\n";
        var row = Assert.Single(Winget.ParseTable(output));
        Assert.Equal("Notepad++.Notepad++", row[1]);
        Assert.Equal("8.6.9", row[3]);
    }

    [Fact]
    public void No_table_is_no_rows() => Assert.Empty(Winget.ParseTable("No installed package found matching input criteria.\n"));

    [Theory]
    [InlineData("00-1A-2B-3C-4D-5E", "00:1a:2b:3c:4d:5e")]
    [InlineData("001a.2b3c.4d5e", "00:1a:2b:3c:4d:5e")]
    [InlineData("00:1a:2b:3c:4d", null)]
    [InlineData("", null)]
    public void A_mac_address_is_kept_in_one_form(string mac, string? expected) => Assert.Equal(expected, InventoryService.NormalizeMac(mac));

    [Fact]
    public void A_secret_is_kept_as_its_hash_and_compared_in_constant_time()
    {
        var secret = EnrollmentService.NewSecret("mhk_");
        Assert.StartsWith("mhk_", secret);
        Assert.Equal(36, secret.Length);
        var hash = EnrollmentService.Hash(secret);
        Assert.True(EnrollmentService.Verify(secret, hash));
        Assert.False(EnrollmentService.Verify(secret + "x", hash));
        Assert.False(EnrollmentService.Verify(null, hash));
    }

    [Theory]
    [InlineData(GroupRuleField.Name, GroupRuleOperator.StartsWith, "nb-", true)]
    [InlineData(GroupRuleField.Name, GroupRuleOperator.Equals, "NB-0042", true)]
    [InlineData(GroupRuleField.OperatingSystem, GroupRuleOperator.Contains, "windows 11", true)]
    [InlineData(GroupRuleField.Tag, GroupRuleOperator.Equals, "laptop", true)]
    [InlineData(GroupRuleField.Tag, GroupRuleOperator.Equals, "lap", false)]
    [InlineData(GroupRuleField.IpAddress, GroupRuleOperator.StartsWith, "10.1.", true)]
    [InlineData(GroupRuleField.Domain, GroupRuleOperator.EndsWith, ".local", false)]
    [InlineData(GroupRuleField.Name, GroupRuleOperator.Contains, "", false)]
    public void A_rule_of_a_dynamic_group_matches_the_facts_of_a_client(GroupRuleField field, GroupRuleOperator op, string value, bool expected)
        => Assert.Equal(expected, GroupService.Matches(field, op, value, "NB-0042", "Microsoft Windows 11 Pro", null, "LENOVO", "20XW", "10.1.2.3, fe80::1", "laptop,finance"));
}
