using System.Xml.Linq;
using MatHub.Data;
using MatHub.Services.Rollout;
using Microsoft.AspNetCore.DataProtection;

namespace MatHub.Tests;

/// <summary>The network boot: the first script, the answer file MatHub writes and the files Windows PE gets.</summary>
public sealed class BootTests
{
    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    private static (RolloutTask Task, RolloutProfile Profile) Sample(string adminPassword = "S3cret&<>\"")
    {
        var protector = Protection.CreateProtector(RolloutService.ProtectorPurpose);
        var profile = new RolloutProfile
        {
            Id = 3, Name = "Windows 11 Pro", Kind = ProfileKind.Windows, SourcePath = @"\\files\deploy\win11", SourceUser = @"LAB\deploy",
            SourcePasswordProtected = protector.Protect("share-pass"), AdminPasswordProtected = protector.Protect(adminPassword), ImageIndex = 6, Locale = "de-DE",
            TimeZone = "W. Europe Standard Time", InstallAgent = true,
        };
        var task = new RolloutTask { Token = Guid.Parse("8f14e45f-ceea-467a-9a07-1a3c1f5e2b11"), MacAddress = "00:1a:2b:3c:4d:5e", HostName = "PC-0042", Profile = profile };
        return (task, profile);
    }

    [Fact]
    public void The_first_script_asks_MatHub_at_the_tftp_server_or_at_the_boot_address()
    {
        var script = BootService.EntryScript(null, 4910);
        Assert.StartsWith("#!ipxe", script);
        Assert.Contains("set mathub http://${next-server}:4910", script);
        Assert.Contains("/boot/ipxe?mac=${netX/mac:hexhyp}", script);
        Assert.Contains("set mathub http://10.0.0.5:8080", BootService.EntryScript("http://10.0.0.5:8080", 4910));
    }

    [Theory]
    [InlineData(true, 3)]
    [InlineData(false, 2)]
    public void The_answer_file_is_valid_xml_with_the_values_of_the_profile(bool uefi, int partition)
    {
        var (task, profile) = Sample();
        var xml = BootService.Unattend(task, profile, uefi, "http://hub:4910", Protection);
        var doc = XDocument.Parse(xml);
        XNamespace u = "urn:schemas-microsoft-com:unattend";
        Assert.Equal("PC-0042", doc.Descendants(u + "ComputerName").Single().Value);
        Assert.Equal("6", doc.Descendants(u + "Value").First().Value);
        Assert.Equal(partition.ToString(System.Globalization.CultureInfo.InvariantCulture), doc.Descendants(u + "InstallTo").Single().Element(u + "PartitionID")!.Value);
        Assert.Equal("S3cret&<>\"", doc.Descendants(u + "AdministratorPassword").Single().Element(u + "Value")!.Value);
        var agent = doc.Descendants(u + "RunSynchronousCommand").Single().Element(u + "Path")!.Value;
        Assert.Contains("/boot/task/8f14e45f-ceea-467a-9a07-1a3c1f5e2b11/agent.ps1", agent);
    }

    [Fact]
    public void The_agent_command_of_the_specialize_pass_fits_into_259_characters()
    {
        var (task, _) = Sample();
        Assert.True(BootService.AgentCommand(task, "https://mathub.example-company.com:4910").Length <= 259);
    }

    [Fact]
    public void Windows_PE_runs_the_setup_from_the_share_of_the_profile()
    {
        var (task, profile) = Sample();
        Assert.Contains("mathub-setup.cmd", BootService.WinPeShell());
        var cmd = BootService.SetupCommand(task, profile, Protection);
        Assert.Contains(@"net use Z: ""\\files\deploy\win11"" /user:""LAB\deploy"" ""share-pass""", cmd);
        Assert.Contains(@"Z:\setup.exe /unattend:X:\Windows\System32\autounattend.xml", cmd);
        Assert.Contains("\r\n", cmd);
    }

    [Fact]
    public void The_placeholders_of_an_answer_file_are_filled_and_unknown_ones_stay()
    {
        var (task, profile) = Sample();
        var text = BootService.Fill("host={hostname} answer={answer} other={unknown} key={productkey}", task, profile, "http://hub", Protection);
        Assert.Equal("host=PC-0042 answer=http://hub/boot/task/8f14e45f-ceea-467a-9a07-1a3c1f5e2b11/answer other={unknown} key=", text);
    }

    [Fact]
    public void A_computer_without_a_task_boots_from_its_disk() => Assert.Contains("exit", BootService.LocalScript("no task"));
}
