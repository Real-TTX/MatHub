using MatHub.Data;
using MatHub.Protocol;
using MatHub.Services.Access;
using MatHub.Services.Agent;
using MatHub.Services.Clients;
using MatHub.Services.Deployments;
using MatHub.Services.Jobs;
using MatHub.Services.Kiosk;
using MatHub.Services.Packages;
using MatHub.Services.Scripts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MatHub.Tests;

/// <summary>The business layer against a real PostgreSQL: enrollment, jobs for the agent, the reach of a role, the planner of the assignments.</summary>
public sealed class HubTests
{
    private static readonly Actor Admin = new(1, true, new HashSet<string>(), true, []);

    private static async Task<string> KeyAsync(TestDatabase t, bool autoApprove = true, long? group = null)
    {
        var saved = await EnrollmentService.SaveAsync(t.Db, Admin, null, new EnrollmentKeyForm("Lab", true, null, 0, autoApprove, group), "Europe/Berlin");
        return saved.Value.Plain!;
    }

    private static async Task<Client> EnrollAsync(TestDatabase t, string key, string host, string machine)
    {
        var outcome = await EnrollmentService.EnrollAsync(t.Db, new EnrollRequest { Key = key, HostName = host, MachineId = machine, AgentVersion = "local-test" });
        Assert.NotNull(outcome.Response);
        return await t.Db.Clients.SingleAsync(c => c.Token == Guid.Parse(outcome.Response!.ClientToken));
    }

    [DbFact]
    public async Task An_agent_enrolls_takes_its_job_and_reports_it()
    {
        await using var t = await TestDatabase.CreateAsync();
        var group = (await GroupService.SaveAsync(t.Db, Admin, null, new GroupForm("Lab", null, null, GroupKind.Static, GroupRuleField.Name, GroupRuleOperator.Contains, null))).Value!;
        var key = await KeyAsync(t, group: group.Id);
        var client = await EnrollAsync(t, key, "PC-1", "machine-1");
        Assert.Equal(ClientStatus.Active, client.Status);
        Assert.True(await t.Db.ClientGroupMembers.AnyAsync(m => m.ClientId == client.Id && m.ClientGroupId == group.Id));
        Assert.Equal("Lab", Assert.Single((await ClientService.GetAsync(t.Db, Admin, client.Id))!.Groups).Name);

        // the same machine id while the agent still calls: the client waits for an approval (a cloned computer, or someone who read the id)
        var twin = await EnrollAsync(t, key, "PC-1", "machine-1");
        Assert.Equal(client.Id, twin.Id);
        Assert.Equal(ClientStatus.Pending, twin.Status);
        await ClientService.SetStatusAsync(t.Db, Admin, client.Id, ClientStatus.Active);

        // a reinstall after the agent stopped finds its client again, with a new secret, approved as it was
        await t.Db.Clients.ExecuteUpdateAsync(s => s.SetProperty(c => c.LastSeenDate, DateTime.UtcNow.AddMinutes(-10)));
        t.Db.ChangeTracker.Clear();
        var again = await EnrollAsync(t, key, "PC-1", "machine-1");
        Assert.Equal(client.Id, again.Id);
        Assert.Equal(ClientStatus.Active, again.Status);
        Assert.Equal(1, await t.Db.Clients.CountAsync());

        var script = (await ScriptService.SaveAsync(t.Db, Admin, null, new ScriptForm("Hello", null, null, ScriptLanguage.PowerShell, ScriptEditor.Code, 60))).Value!;
        await ScriptService.SaveContentAsync(t.Db, Admin, script.Id, "Write-Output 'hello'", null);
        var job = await JobService.CreateAsync(t.Db, Admin, new JobRequest(JobKind.Script, script.Id, null, null, [], [group.Id]));
        Assert.True(job.Ok, job.Error);

        var taken = await JobService.TakeAsync(t.Db, client.Id, 5);
        var agentJob = Assert.Single(taken);
        Assert.Equal(JobKinds.Script, agentJob.Kind);
        Assert.Equal("Write-Output 'hello'", agentJob.Script);
        Assert.Empty(await JobService.TakeAsync(t.Db, client.Id, 5));

        Assert.True(await JobService.CompleteAsync(t.Db, client.Id, agentJob.RunId, new RunResult { Succeeded = true, ExitCode = 0, Output = "hello" }));
        var run = await t.Db.JobRuns.AsNoTracking().SingleAsync();
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal("hello", run.Output);
        Assert.True((await t.Db.Clients.AsNoTracking().SingleAsync()).InventoryRequested);

        // another client cannot report the run
        Assert.False(await JobService.CompleteAsync(t.Db, client.Id + 1, agentJob.RunId, new RunResult { Succeeded = false }));
    }

    [DbFact]
    public async Task A_key_that_needs_an_approval_lets_the_client_wait()
    {
        await using var t = await TestDatabase.CreateAsync();
        var key = await KeyAsync(t, autoApprove: false);
        var client = await EnrollAsync(t, key, "PC-2", "machine-2");
        Assert.Equal(ClientStatus.Pending, client.Status);
        Assert.False((await JobService.CreateAsync(t.Db, Admin, new JobRequest(JobKind.Inventory, null, null, null, [client.Id], []))).Ok);
        Assert.True((await ClientService.SetStatusAsync(t.Db, Admin, client.Id, ClientStatus.Active)).Ok);
        Assert.True((await JobService.CreateAsync(t.Db, Admin, new JobRequest(JobKind.Inventory, null, null, null, [client.Id], []))).Ok);

        var refused = await EnrollmentService.EnrollAsync(t.Db, new EnrollRequest { Key = "mhk_wrong", HostName = "PC-3" });
        Assert.Null(refused.Response);
        Assert.Equal(403, refused.Status);
    }

    [DbFact]
    public async Task A_role_for_a_group_reaches_only_its_clients()
    {
        await using var t = await TestDatabase.CreateAsync();
        var office = (await GroupService.SaveAsync(t.Db, Admin, null, new GroupForm("Office", null, null, GroupKind.Dynamic, GroupRuleField.Name, GroupRuleOperator.StartsWith, "OF-"))).Value!;
        var key = await KeyAsync(t);
        var inOffice = await EnrollAsync(t, key, "OF-1", "m-of-1");
        var elsewhere = await EnrollAsync(t, key, "LAB-1", "m-lab-1");
        Assert.True(await t.Db.ClientGroupMembers.AnyAsync(m => m.ClientId == inOffice.Id && m.ClientGroupId == office.Id));

        var helpdesk = new Actor(2, false, new HashSet<string> { Permission.ClientsView, Permission.JobsView, Permission.ClientsManage }, false, [office.Id]);
        var list = await ClientService.ListAsync(t.Db, helpdesk, new ClientCriteria(null, null, null, null, "name", false, 1, 50));
        Assert.Equal("OF-1", Assert.Single(list!.Items).Name);
        Assert.Null(await ClientService.GetAsync(t.Db, helpdesk, elsewhere.Id));
        Assert.True((await ClientService.DeleteAsync(t.Db, helpdesk, elsewhere.Id)).NotAllowed);

        // the permission decides, not the page: without scripts.run no script job, without packages.deploy no update
        Assert.True((await JobService.CreateAsync(t.Db, helpdesk, new JobRequest(JobKind.WingetUpgrade, null, null, null, [inOffice.Id], []))).NotAllowed);

        // a restart on a client outside the role is no target
        var restart = await JobService.CreateAsync(t.Db, helpdesk, new JobRequest(JobKind.Restart, null, null, null, [elsewhere.Id], []));
        Assert.False(restart.Ok);

        var viewer = new Actor(3, false, new HashSet<string>(), true, []);
        Assert.Null(await ClientService.ListAsync(t.Db, viewer, new ClientCriteria(null, null, null, null, "name", false, 1, 50)));
    }

    [DbFact]
    public async Task A_group_with_several_conditions_takes_all_of_them_or_one_of_them()
    {
        await using var t = await TestDatabase.CreateAsync();
        GroupCondition[] conditions = [new(GroupRuleField.Name, GroupRuleOperator.StartsWith, "BER-"), new(GroupRuleField.Name, GroupRuleOperator.Contains, "-NB-")];
        var all = (await GroupService.SaveAsync(t.Db, Admin, null, new GroupForm("Berlin laptops", null, null, GroupKind.Dynamic, conditions))).Value!;
        var any = (await GroupService.SaveAsync(t.Db, Admin, null, new GroupForm("Berlin or laptops", null, null, GroupKind.Dynamic, conditions, MatchAny: true))).Value!;
        var key = await KeyAsync(t);
        var berlinLaptop = await EnrollAsync(t, key, "BER-NB-1", "m-1");
        var berlinDesktop = await EnrollAsync(t, key, "BER-PC-1", "m-2");
        var hamburgLaptop = await EnrollAsync(t, key, "HAM-NB-1", "m-3");

        async Task<long[]> MembersOf(ClientGroup g) => await t.Db.ClientGroupMembers.Where(m => m.ClientGroupId == g.Id).Select(m => m.ClientId).OrderBy(x => x).ToArrayAsync();
        Assert.Equal([berlinLaptop.Id], await MembersOf(all));
        Assert.Equal(new[] { berlinLaptop.Id, berlinDesktop.Id, hamburgLaptop.Id }.Order(), await MembersOf(any));

        // the conditions come back in their order, and the list says them in words
        Assert.Equal(conditions, GroupService.Conditions((await GroupService.GetAsync(t.Db, Admin, all.Id))!));
        var row = (await GroupService.ListAsync(t.Db, Admin, "Berlin laptops"))!.Single();
        Assert.Contains(MatHub.Localization.Text.T("Groups.And"), row.Rule, StringComparison.Ordinal);

        // a dynamic group without a condition is refused
        Assert.False((await GroupService.SaveAsync(t.Db, Admin, null, new GroupForm("Empty", null, null, GroupKind.Dynamic, [new GroupCondition(GroupRuleField.Name, GroupRuleOperator.Contains, " ")]))).Ok);
    }

    private static async Task<Script> ScriptAsync(TestDatabase t, string name, string code)
    {
        var script = (await ScriptService.SaveAsync(t.Db, Admin, null, new ScriptForm(name, null, null, ScriptLanguage.PowerShell, ScriptEditor.Code, 60))).Value!;
        await ScriptService.SaveContentAsync(t.Db, Admin, script.Id, code, null);
        return script;
    }

    [DbFact]
    public async Task A_sequence_hands_out_its_steps_one_by_one_and_waits_for_the_restart()
    {
        await using var t = await TestDatabase.CreateAsync();
        var client = await EnrollAsync(t, await KeyAsync(t), "PC-SEQ", "m-seq");
        var first = await ScriptAsync(t, "Prepare", "Write-Output 'one'");
        var last = await ScriptAsync(t, "Finish", "Write-Output 'three'");
        var sequence = (await SequenceService.SaveAsync(t.Db, Admin, null, new SequenceForm("Set-up", null, null,
        [
            new SequenceStep { Kind = SequenceStepKind.Script, ScriptId = first.Id },
            new SequenceStep { Kind = SequenceStepKind.Restart },
            new SequenceStep { Kind = SequenceStepKind.Script, ScriptId = last.Id },
        ]))).Value!;
        var job = await JobService.CreateAsync(t.Db, Admin, new JobRequest(JobKind.Sequence, null, null, null, [client.Id], [], SequenceId: sequence.Id));
        Assert.True(job.Ok, job.Error);

        // one step at a time
        var step1 = Assert.Single(await JobService.TakeAsync(t.Db, client.Id, 5));
        Assert.Equal("Write-Output 'one'", step1.Script);
        Assert.Empty(await JobService.TakeAsync(t.Db, client.Id, 5));
        Assert.True(await JobService.CompleteAsync(t.Db, client.Id, step1.RunId, new RunResult { Succeeded = true, ExitCode = 0, Output = "one" }));

        var step2 = Assert.Single(await JobService.TakeAsync(t.Db, client.Id, 5));
        Assert.Equal(JobKinds.Restart, step2.Kind);
        Assert.Equal(step1.RunId, step2.RunId);
        Assert.True(await JobService.CompleteAsync(t.Db, client.Id, step2.RunId, new RunResult { Succeeded = true, ExitCode = 0 }));

        // the computer has not started again yet: nothing to do; after its new boot the last step comes
        Assert.Empty(await JobService.TakeAsync(t.Db, client.Id, 5));
        await t.Db.Clients.Where(c => c.Id == client.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.BootDate, DateTime.UtcNow.AddMinutes(1)));
        var step3 = Assert.Single(await JobService.TakeAsync(t.Db, client.Id, 5));
        Assert.Equal("Write-Output 'three'", step3.Script);
        Assert.Contains("Finish", step3.Title);
        Assert.Equal(RunStates.Running, (await JobService.StateAsync(t.Db, client.Id, step3.RunId))!.Status);
        Assert.Equal(3, (await JobService.StateAsync(t.Db, client.Id, step3.RunId))!.Step);
        Assert.True(await JobService.CompleteAsync(t.Db, client.Id, step3.RunId, new RunResult { Succeeded = true, ExitCode = 0, Output = "three" }));

        var run = await t.Db.JobRuns.AsNoTracking().SingleAsync();
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Contains("=== 1/3 Prepare", run.Output);
        Assert.Contains("=== 3/3 Finish", run.Output);

        // a failed step ends the run, unless it may fail
        var again = await JobService.CreateAsync(t.Db, Admin, new JobRequest(JobKind.Sequence, null, null, null, [client.Id], [], SequenceId: sequence.Id));
        var failed = Assert.Single(await JobService.TakeAsync(t.Db, client.Id, 5));
        await JobService.CompleteAsync(t.Db, client.Id, failed.RunId, new RunResult { Succeeded = false, ExitCode = 1, Output = "boom" });
        Assert.Equal(RunStatus.Failed, (await t.Db.JobRuns.AsNoTracking().SingleAsync(r => r.JobId == again.Value!.Id)).Status);
    }

    [DbFact]
    public async Task The_kiosk_offers_to_its_groups_and_starts_on_the_computer_that_asks()
    {
        await using var t = await TestDatabase.CreateAsync();
        var office = (await GroupService.SaveAsync(t.Db, Admin, null, new GroupForm("Office", null, null, GroupKind.Dynamic, GroupRuleField.Name, GroupRuleOperator.StartsWith, "OF-"))).Value!;
        var key = await KeyAsync(t);
        var inOffice = await EnrollAsync(t, key, "OF-1", "m-of-k");
        var elsewhere = await EnrollAsync(t, key, "LAB-1", "m-lab-k");
        var printers = await ScriptAsync(t, "Printers", "Write-Output 'printers'");
        var item = await KioskService.SaveAsync(t.Db, Admin, null, new KioskForm("Connect the printers", "Of your floor", "Office", "printer", KioskKind.Script, printers.Id, null, null, [office.Id], null, true, 10));
        Assert.True(item.Ok, item.Error);
        var hidden = await KioskService.SaveAsync(t.Db, Admin, null, new KioskForm("Hidden", null, null, "box", KioskKind.Script, printers.Id, null, null, [], null, false, 20));
        Assert.True(hidden.Ok);

        Assert.Equal("Connect the printers", Assert.Single((await KioskService.OfferAsync(t.Db, inOffice)).Items).Name);
        Assert.Empty((await KioskService.OfferAsync(t.Db, elsewhere)).Items);
        Assert.True((await KioskService.StartAsync(t.Db, elsewhere, item.Value!.Id, "LAB\\someone")).NotAllowed);

        var started = await KioskService.StartAsync(t.Db, inOffice, item.Value.Id, "CONTOSO\\anna");
        Assert.True(started.Ok, started.Error);
        Assert.Equal(started.Value, (await KioskService.StartAsync(t.Db, inOffice, item.Value.Id, "CONTOSO\\anna")).Value);
        var job = await t.Db.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal("kiosk", job.Origin);
        Assert.Equal(item.Value.Id, job.KioskItemId);
        Assert.Equal(RunStates.Queued, (await JobService.StateAsync(t.Db, inOffice.Id, started.Value))!.Status);
        Assert.Null(await JobService.StateAsync(t.Db, elsewhere.Id, started.Value));
        Assert.Equal(RunStates.Queued, Assert.Single((await KioskService.OfferAsync(t.Db, inOffice)).Items).Last!.Status);

        // who may not run scripts cannot offer one either
        var editor = new Actor(5, false, new HashSet<string> { Permission.KioskManage, Permission.ScriptsView }, true, []);
        Assert.False((await KioskService.SaveAsync(t.Db, editor, null, new KioskForm("X", null, null, "box", KioskKind.Script, printers.Id, null, null, [], null, true, 0))).Ok);
    }

    [DbFact]
    public async Task A_secret_reaches_only_the_run_of_a_script_that_names_it()
    {
        await using var t = await TestDatabase.CreateAsync();
        var protection = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
        var saved = await SecretService.SaveAsync(t.Db, Admin, protection, null, new SecretForm("domain join", "The account of the joins", "CONTOSO\\svc-join", "S3cret!"));
        Assert.True(saved.Ok, saved.Error);
        Assert.Equal("DOMAIN_JOIN", saved.Value!.Name);
        Assert.DoesNotContain("S3cret", saved.Value.ValueProtected);
        Assert.False((await SecretService.SaveAsync(t.Db, Admin, protection, null, new SecretForm("DOMAIN_JOIN", null, null, "x"))).Ok);

        var client = await EnrollAsync(t, await KeyAsync(t), "PC-SEC", "m-sec");
        var join = await ScriptAsync(t, "Join", "$c = New-MatHubCredential 'DOMAIN_JOIN' ''\nAdd-Computer -DomainName contoso.local -Credential $c");
        var plain = await ScriptAsync(t, "Plain", "Write-Output 'no secret'");
        await JobService.CreateAsync(t.Db, Admin, new JobRequest(JobKind.Script, join.Id, null, null, [client.Id], []));
        await JobService.CreateAsync(t.Db, Admin, new JobRequest(JobKind.Script, plain.Id, null, null, [client.Id], []));
        var taken = await JobService.TakeAsync(t.Db, client.Id, 5, protection: protection);
        Assert.Equal(2, taken.Count);
        var secrets = taken.Single(j => j.Script!.Contains("Add-Computer", StringComparison.Ordinal)).Secrets!;
        Assert.Equal("S3cret!", secrets["DOMAIN_JOIN"]);
        Assert.Equal("CONTOSO\\svc-join", secrets["DOMAIN_JOIN_USER"]);
        Assert.Null(taken.Single(j => j.Script!.Contains("no secret", StringComparison.Ordinal)).Secrets);

        // the job keeps no value
        Assert.DoesNotContain(await t.Db.Jobs.AsNoTracking().Select(j => j.Payload).ToListAsync(), p => p.Contains("S3cret", StringComparison.Ordinal));
        Assert.Equal(1, Assert.Single((await SecretService.ListAsync(t.Db, Admin, null))!).Scripts);
    }

    [DbFact]
    public async Task A_test_from_the_editor_runs_what_is_in_it_on_one_computer()
    {
        await using var t = await TestDatabase.CreateAsync();
        var client = await EnrollAsync(t, await KeyAsync(t), "PC-TEST", "m-test");
        var script = await ScriptAsync(t, "Saved", "Write-Output 'saved'");
        var started = await ScriptTester.StartAsync(t.Db, Admin, script.Id, new ScriptTest(client.Id, false, "Write-Output 'not saved yet'", null));
        Assert.True(started.Ok, started.Error);
        var job = await t.Db.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal("test", job.Origin);
        Assert.Equal("Write-Output 'not saved yet'", Assert.Single(await JobService.TakeAsync(t.Db, client.Id, 5)).Script);
        Assert.Equal("Write-Output 'saved'", (await t.Db.Scripts.AsNoTracking().SingleAsync()).Content);

        var trial = await ScriptTester.StartAsync(t.Db, Admin, script.Id, new ScriptTest(client.Id, true, null, null));
        Assert.True(trial.Ok);
        Assert.Contains("ParseInput", Assert.Single(await JobService.TakeAsync(t.Db, client.Id, 5)).Script);
        Assert.Equal(RunStatus.Running, (await ScriptTester.StateAsync(t.Db, Admin, trial.Value!.RunId))!.Status);

        // running what is not saved needs the right to edit scripts
        var runner = new Actor(7, false, new HashSet<string> { Permission.ScriptsView, Permission.ScriptsRun, Permission.JobsView }, true, []);
        Assert.True((await ScriptTester.StartAsync(t.Db, runner, script.Id, new ScriptTest(client.Id, true, "x", null))).NotAllowed);
        Assert.True((await ScriptTester.StartAsync(t.Db, runner, script.Id, new ScriptTest(client.Id, true, null, null))).Ok);
    }

    [DbFact]
    public async Task The_files_of_a_script_go_with_its_run_and_only_to_that_run()
    {
        await using var t = await TestDatabase.CreateAsync();
        var store = new ScriptFileStore(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataDirectory"] = Path.Combine(Path.GetTempPath(), "mathub-test-" + Guid.NewGuid().ToString("N")) }).Build());
        var client = await EnrollAsync(t, await KeyAsync(t), "PC-FILES", "m-files");
        var script = await ScriptAsync(t, "With a file", "Copy-Item (Join-Path $env:MATHUB_FILES 'settings.xml') C:\\Temp");
        var other = await ScriptAsync(t, "Without", "Write-Output 1");
        var uploaded = await ScriptFileService.UploadAsync(t.Db, Admin, store, script.Id, "C:\\fakepath\\settings.xml", new MemoryStream("<a/>"u8.ToArray()));
        Assert.True(uploaded.Ok, uploaded.Error);
        Assert.Equal("settings.xml", uploaded.Value!.Name);
        Assert.True(File.Exists(store.PathOf(script.Id, "settings.xml")));

        await JobService.CreateAsync(t.Db, Admin, new JobRequest(JobKind.Script, other.Id, null, null, [client.Id], []));
        var first = Assert.Single(await JobService.TakeAsync(t.Db, client.Id, 1));
        Assert.Null(first.Files);
        Assert.False(await ScriptFileService.AllowedAsync(t.Db, client.Id, uploaded.Value));

        await JobService.CreateAsync(t.Db, Admin, new JobRequest(JobKind.Script, script.Id, null, null, [client.Id], []));
        var job = Assert.Single(await JobService.TakeAsync(t.Db, client.Id, 5));
        var file = Assert.Single(job.Files!);
        Assert.Equal("settings.xml", file.Name);
        Assert.Equal(uploaded.Value.Sha256, file.Sha256);
        Assert.True(await ScriptFileService.AllowedAsync(t.Db, client.Id, uploaded.Value));
        await JobService.CompleteAsync(t.Db, client.Id, job.RunId, new RunResult { Succeeded = true });
        Assert.False(await ScriptFileService.AllowedAsync(t.Db, client.Id, uploaded.Value));
    }

    private static async Task InventoryAsync(TestDatabase t, Client c, params (string Name, string Id, string Version)[] programs)
        => await InventoryService.ApplyAsync(t.Db, await t.Db.Clients.SingleAsync(x => x.Id == c.Id), new InventoryReport
        {
            HostName = c.Name,
            Software = programs.Select(p => new SoftwareInfo(p.Name, p.Version, "Contoso")).ToList(),
            WingetInstalled = programs.Select(p => new WingetPackageInfo(p.Name, p.Id, p.Version)).ToList(),
            WingetUpgrades = [],
        });

    private static async Task<Package> PackageWithVersionAsync(TestDatabase t, string version, long? pilot = null)
    {
        var package = (await PackageService.SaveAsync(t.Db, Admin, null, new PackageForm("Editor", "Contoso", null, "Contoso.Editor", DetectionKind.WingetId, null, false, pilot))).Value!;
        var v = (await PackageVersions.CreateAsync(t.Db, Admin, package.Id, version, "upload")).Value!;
        Assert.True((await PackageVersions.AddInstallerAsync(t.Db, Admin, null!, v.Id, new InstallerForm(InstallerArchitecture.Neutral, PackageKind.Winget, WingetId: "Contoso.Editor", WingetVersion: version), null, null)).Ok);
        Assert.True((await PackageVersions.SetStatusAsync(t.Db, Admin, v.Id, VersionStatus.Approved)).Ok);
        return package;
    }

    [DbFact]
    public async Task The_planner_brings_only_the_approved_version_and_updates_what_is_older()
    {
        await using var t = await TestDatabase.CreateAsync();
        var group = (await GroupService.SaveAsync(t.Db, Admin, null, new GroupForm("All", null, null, GroupKind.Dynamic, GroupRuleField.Name, GroupRuleOperator.StartsWith, "PC-"))).Value!;
        var key = await KeyAsync(t);
        var missing = await EnrollAsync(t, key, "PC-A", "m-a");
        var older = await EnrollAsync(t, key, "PC-B", "m-b");
        var current = await EnrollAsync(t, key, "PC-C", "m-c");
        await InventoryAsync(t, missing);
        await InventoryAsync(t, older, ("Contoso Editor", "Contoso.Editor", "9.11"));
        await InventoryAsync(t, current, ("Contoso Editor", "Contoso.Editor", "10.2"));

        var package = await PackageWithVersionAsync(t, "10.2");
        Assert.True((await PackageService.AssignAsync(t.Db, Admin, package.Id, group.Id, AssignmentAction.Install)).Ok);

        // the missing one and the older one get 10.2 (10.2 is newer than 9.11), the current one nothing
        var plan = await Planner.RunOnceAsync(t.Db);
        Assert.Equal(1, plan.Jobs);
        var job = await t.Db.Jobs.AsNoTracking().Include(j => j.Runs).SingleAsync();
        Assert.Equal(new[] { missing.Id, older.Id }.Order(), job.Runs.Select(r => r.ClientId).Order());
        var template = JobService.Template(job)!;
        Assert.Equal("10.2", template.Version);
        Assert.Equal(0, (await Planner.RunOnceAsync(t.Db)).Jobs);

        // a new version nobody approved goes nowhere
        await t.Db.JobRuns.ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatus.Cancelled));
        var newer = (await PackageVersions.CreateAsync(t.Db, Admin, package.Id, "11.0", "catalog")).Value!;
        await PackageVersions.AddInstallerAsync(t.Db, Admin, null!, newer.Id, new InstallerForm(InstallerArchitecture.Neutral, PackageKind.Winget, WingetId: "Contoso.Editor", WingetVersion: "11.0"), null, null);
        await Planner.RunOnceAsync(t.Db);
        Assert.DoesNotContain(await t.Db.Jobs.AsNoTracking().ToListAsync(), j => j.PackageVersionId == newer.Id);

        // approved: it replaces 10.2 (retired) and goes to every client, also the current one
        Assert.True((await PackageVersions.SetStatusAsync(t.Db, Admin, newer.Id, VersionStatus.Approved)).Ok);
        Assert.Equal("11.0", (await t.Db.Packages.AsNoTracking().SingleAsync()).Version);
        Assert.Equal(VersionStatus.Retired, (await t.Db.PackageVersions.AsNoTracking().SingleAsync(v => v.Version == "10.2")).Status);
        await t.Db.JobRuns.ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatus.Cancelled));
        await Planner.RunOnceAsync(t.Db);
        var update = await t.Db.Jobs.AsNoTracking().Include(j => j.Runs).SingleAsync(j => j.PackageVersionId == newer.Id);
        Assert.Equal(3, update.Runs.Count);
    }

    [DbFact]
    public async Task A_package_from_before_the_versions_is_not_installed_again_and_again()
    {
        await using var t = await TestDatabase.CreateAsync();
        var group = (await GroupService.SaveAsync(t.Db, Admin, null, new GroupForm("All", null, null, GroupKind.Dynamic, GroupRuleField.Name, GroupRuleOperator.StartsWith, "PC-"))).Value!;
        var client = await EnrollAsync(t, await KeyAsync(t), "PC-L", "m-l");
        await InventoryAsync(t, client, ("Contoso Editor", "Contoso.Editor", "131.0"));
        var package = (await PackageService.SaveAsync(t.Db, Admin, null, new PackageForm("Editor", "Contoso", null, "Contoso.Editor", DetectionKind.WingetId, null, false))).Value!;
        var v = (await PackageVersions.CreateAsync(t.Db, Admin, package.Id, "latest", "migrated")).Value!;
        await PackageVersions.AddInstallerAsync(t.Db, Admin, null!, v.Id, new InstallerForm(InstallerArchitecture.Neutral, PackageKind.Winget, WingetId: "Contoso.Editor"), null, null);
        await PackageVersions.SetStatusAsync(t.Db, Admin, v.Id, VersionStatus.Approved);
        await PackageService.AssignAsync(t.Db, Admin, package.Id, group.Id, AssignmentAction.Install);
        Assert.Equal(0, (await Planner.RunOnceAsync(t.Db)).Jobs);
    }

    [DbFact]
    public async Task A_version_in_test_goes_to_the_pilot_group_only()
    {
        await using var t = await TestDatabase.CreateAsync();
        var all = (await GroupService.SaveAsync(t.Db, Admin, null, new GroupForm("All", null, null, GroupKind.Dynamic, GroupRuleField.Name, GroupRuleOperator.StartsWith, "PC-"))).Value!;
        var pilot = (await GroupService.SaveAsync(t.Db, Admin, null, new GroupForm("Pilot", null, null, GroupKind.Dynamic, GroupRuleField.Name, GroupRuleOperator.StartsWith, "PC-IT"))).Value!;
        var key = await KeyAsync(t);
        var it = await EnrollAsync(t, key, "PC-IT1", "m-it");
        var office = await EnrollAsync(t, key, "PC-OF1", "m-of");
        await InventoryAsync(t, it, ("Contoso Editor", "Contoso.Editor", "10.2"));
        await InventoryAsync(t, office, ("Contoso Editor", "Contoso.Editor", "10.2"));
        var package = await PackageWithVersionAsync(t, "10.2", pilot.Id);
        await PackageService.AssignAsync(t.Db, Admin, package.Id, all.Id, AssignmentAction.Install);

        var test = (await PackageVersions.CreateAsync(t.Db, Admin, package.Id, "11.0-beta", "upload")).Value!;
        await PackageVersions.AddInstallerAsync(t.Db, Admin, null!, test.Id, new InstallerForm(InstallerArchitecture.Neutral, PackageKind.Winget, WingetId: "Contoso.Editor", WingetVersion: "11.0-beta"), null, null);
        Assert.True((await PackageVersions.SetStatusAsync(t.Db, Admin, test.Id, VersionStatus.Testing)).Ok);
        await Planner.RunOnceAsync(t.Db);
        var job = await t.Db.Jobs.AsNoTracking().Include(j => j.Runs).SingleAsync();
        Assert.Equal(test.Id, job.PackageVersionId);
        Assert.Equal(it.Id, Assert.Single(job.Runs).ClientId);

        // without a pilot group a version in test goes to nobody by itself
        var bare = await PackageWithVersionAsync(t, "10.2");
        await PackageService.AssignAsync(t.Db, Admin, bare.Id, all.Id, AssignmentAction.Install);
        var another = (await PackageVersions.CreateAsync(t.Db, Admin, bare.Id, "11.0", "upload")).Value!;
        await PackageVersions.AddInstallerAsync(t.Db, Admin, null!, another.Id, new InstallerForm(InstallerArchitecture.Neutral, PackageKind.Winget, WingetId: "Contoso.Editor", WingetVersion: "11.0"), null, null);
        Assert.True((await PackageVersions.SetStatusAsync(t.Db, Admin, another.Id, VersionStatus.Testing)).Ok);
        await Planner.RunOnceAsync(t.Db);
        Assert.False(await t.Db.Jobs.AnyAsync(j => j.PackageVersionId == another.Id));
    }

    [DbFact]
    public async Task A_job_takes_the_approved_version_or_the_one_in_test_never_a_new_one()
    {
        await using var t = await TestDatabase.CreateAsync();
        var client = await EnrollAsync(t, await KeyAsync(t), "PC-1", "m-1");
        var package = await PackageWithVersionAsync(t, "10.2");
        var fresh = (await PackageVersions.CreateAsync(t.Db, Admin, package.Id, "11.0", "catalog")).Value!;
        await PackageVersions.AddInstallerAsync(t.Db, Admin, null!, fresh.Id, new InstallerForm(InstallerArchitecture.Neutral, PackageKind.Winget, WingetId: "Contoso.Editor", WingetVersion: "11.0"), null, null);
        JobRequest Install(long? version, JobKind kind = JobKind.PackageInstall) => new(kind, null, package.Id, null, [client.Id], [], PackageVersionId: version);

        Assert.Equal("10.2", (await t.Db.PackageVersions.FindAsync((await JobService.CreateAsync(t.Db, Admin, Install(null))).Value!.PackageVersionId))!.Version);
        Assert.False((await JobService.CreateAsync(t.Db, Admin, Install(fresh.Id))).Ok);
        Assert.True((await PackageVersions.SetStatusAsync(t.Db, Admin, fresh.Id, VersionStatus.Testing)).Ok);
        Assert.Equal(fresh.Id, (await JobService.CreateAsync(t.Db, Admin, Install(fresh.Id))).Value!.PackageVersionId);

        // retired: no install, but an uninstall removes what is still there
        var old = await t.Db.PackageVersions.AsNoTracking().SingleAsync(v => v.Version == "10.2");
        Assert.True((await PackageVersions.SetStatusAsync(t.Db, Admin, fresh.Id, VersionStatus.Approved)).Ok);
        Assert.False((await JobService.CreateAsync(t.Db, Admin, Install(old.Id))).Ok);
        Assert.True((await JobService.CreateAsync(t.Db, Admin, Install(old.Id, JobKind.PackageUninstall))).Ok);

        // the choices of a job: the approved version, the one in test as a choice of its own
        var choices = await PackageVersions.JobChoicesAsync(t.Db, Admin);
        Assert.Equal([package.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)], choices.Select(c => c.Value));
        Assert.Equal((package.Id, (long?)fresh.Id), JobVersionChoice.Parse(package.Id + "@" + fresh.Id));
    }

    [DbFact]
    public async Task The_planner_does_not_install_again_what_the_inventory_does_not_confirm()
    {
        await using var t = await TestDatabase.CreateAsync();
        var all = (await GroupService.SaveAsync(t.Db, Admin, null, new GroupForm("All", null, null, GroupKind.Dynamic, GroupRuleField.Name, GroupRuleOperator.StartsWith, "PC-"))).Value!;
        var client = await EnrollAsync(t, await KeyAsync(t), "PC-1", "m-1");
        await InventoryAsync(t, client);
        var package = await PackageWithVersionAsync(t, "11.0");
        await PackageService.AssignAsync(t.Db, Admin, package.Id, all.Id, AssignmentAction.Install);
        Assert.Equal(1, (await Planner.RunOnceAsync(t.Db)).Jobs);

        // the install works, but the program reports another version (or none): after the next inventory nothing is tried again
        var run = Assert.Single(await JobService.TakeAsync(t.Db, client.Id, 1));
        Assert.True(await JobService.CompleteAsync(t.Db, client.Id, run.RunId, new RunResult { Succeeded = true, ExitCode = 0 }));
        await InventoryAsync(t, client, ("Contoso Editor", "Contoso.Editor", "10.9"));
        Assert.Equal(0, (await Planner.RunOnceAsync(t.Db)).Jobs);

        // a newer approved version is tried once again
        var newer = (await PackageVersions.CreateAsync(t.Db, Admin, package.Id, "12.0", "upload")).Value!;
        await PackageVersions.AddInstallerAsync(t.Db, Admin, null!, newer.Id, new InstallerForm(InstallerArchitecture.Neutral, PackageKind.Winget, WingetId: "Contoso.Editor", WingetVersion: "12.0"), null, null);
        await PackageVersions.SetStatusAsync(t.Db, Admin, newer.Id, VersionStatus.Approved);
        Assert.Equal(1, (await Planner.RunOnceAsync(t.Db)).Jobs);
    }

    [DbFact]
    public async Task A_removal_wins_over_an_install_and_takes_the_installer_of_the_version_there()
    {
        await using var t = await TestDatabase.CreateAsync();
        var all = (await GroupService.SaveAsync(t.Db, Admin, null, new GroupForm("All", null, null, GroupKind.Dynamic, GroupRuleField.Name, GroupRuleOperator.StartsWith, "PC-"))).Value!;
        var kiosk = (await GroupService.SaveAsync(t.Db, Admin, null, new GroupForm("Kiosk", null, null, GroupKind.Dynamic, GroupRuleField.Name, GroupRuleOperator.StartsWith, "PC-K"))).Value!;
        var key = await KeyAsync(t);
        var office = await EnrollAsync(t, key, "PC-O1", "m-o1");
        var terminal = await EnrollAsync(t, key, "PC-K1", "m-k1");
        await InventoryAsync(t, office);
        await InventoryAsync(t, terminal, ("Contoso Editor", "Contoso.Editor", "10.2"));
        var package = await PackageWithVersionAsync(t, "10.2");
        var newer = (await PackageVersions.CreateAsync(t.Db, Admin, package.Id, "11.0", "upload")).Value!;
        await PackageVersions.AddInstallerAsync(t.Db, Admin, null!, newer.Id, new InstallerForm(InstallerArchitecture.Neutral, PackageKind.Winget, WingetId: "Contoso.Editor", WingetVersion: "11.0"), null, null);
        await PackageVersions.SetStatusAsync(t.Db, Admin, newer.Id, VersionStatus.Approved);
        await PackageService.AssignAsync(t.Db, Admin, package.Id, all.Id, AssignmentAction.Install);
        await PackageService.AssignAsync(t.Db, Admin, package.Id, kiosk.Id, AssignmentAction.Uninstall);

        await Planner.RunOnceAsync(t.Db);
        var jobs = await t.Db.Jobs.AsNoTracking().Include(j => j.Runs).ToListAsync();
        Assert.Equal(2, jobs.Count);
        var install = Assert.Single(jobs, j => j.Kind == JobKind.PackageInstall);
        Assert.Equal(office.Id, Assert.Single(install.Runs).ClientId);
        var removal = Assert.Single(jobs, j => j.Kind == JobKind.PackageUninstall);
        Assert.Equal(terminal.Id, Assert.Single(removal.Runs).ClientId);
        Assert.Equal("10.2", (await t.Db.PackageVersions.FindAsync(removal.PackageVersionId))!.Version);

        // nothing twice while they run
        Assert.Equal(0, (await Planner.RunOnceAsync(t.Db)).Jobs);
    }

    [DbFact]
    public async Task A_package_without_detection_remembers_what_it_installed_after_its_jobs_are_gone()
    {
        await using var t = await TestDatabase.CreateAsync();
        var all = (await GroupService.SaveAsync(t.Db, Admin, null, new GroupForm("All", null, null, GroupKind.Dynamic, GroupRuleField.Name, GroupRuleOperator.StartsWith, "PC-"))).Value!;
        var client = await EnrollAsync(t, await KeyAsync(t), "PC-1", "m-1");
        await InventoryAsync(t, client);
        var package = (await PackageService.SaveAsync(t.Db, Admin, null, new PackageForm("Fonts", "Contoso", null, null, DetectionKind.None, null, false))).Value!;
        var v = (await PackageVersions.CreateAsync(t.Db, Admin, package.Id, "1.0", "upload")).Value!;
        await PackageVersions.AddInstallerAsync(t.Db, Admin, null!, v.Id, new InstallerForm(InstallerArchitecture.Neutral, PackageKind.Script, InstallScript: "Write-Output fonts"), null, null);
        await PackageVersions.SetStatusAsync(t.Db, Admin, v.Id, VersionStatus.Approved);
        await PackageService.AssignAsync(t.Db, Admin, package.Id, all.Id, AssignmentAction.Install);
        Assert.Equal(1, (await Planner.RunOnceAsync(t.Db)).Jobs);
        var run = Assert.Single(await JobService.TakeAsync(t.Db, client.Id, 1));
        await JobService.CompleteAsync(t.Db, client.Id, run.RunId, new RunResult { Succeeded = true, ExitCode = 0 });
        await InventoryAsync(t, client);

        // housekeeping deleted the jobs: still installed
        await t.Db.Jobs.ExecuteDeleteAsync();
        Assert.Equal(0, (await Planner.RunOnceAsync(t.Db)).Jobs);
        Assert.True((await t.Db.ClientPackages.AsNoTracking().SingleAsync()).Installed);
    }

    [DbFact]
    public async Task Versions_keep_their_state_and_their_installers()
    {
        await using var t = await TestDatabase.CreateAsync();
        var package = await PackageWithVersionAsync(t, "1.0");
        var second = (await PackageVersions.CreateAsync(t.Db, Admin, package.Id, "2.0", "upload")).Value!;
        await PackageVersions.AddInstallerAsync(t.Db, Admin, null!, second.Id, new InstallerForm(InstallerArchitecture.Neutral, PackageKind.Winget, WingetId: "Contoso.Editor", WingetVersion: "2.0"), null, null);
        await PackageVersions.SetStatusAsync(t.Db, Admin, second.Id, VersionStatus.Approved);

        // retiring the approved one does not bring the retired 1.0 back as new
        await PackageVersions.SetStatusAsync(t.Db, Admin, second.Id, VersionStatus.Retired);
        Assert.All(await t.Db.PackageVersions.AsNoTracking().ToListAsync(), x => Assert.Equal(VersionStatus.Retired, x.Status));

        // a version that was distributed takes no other installer
        Assert.False((await PackageVersions.AddInstallerAsync(t.Db, Admin, null!, second.Id, new InstallerForm(InstallerArchitecture.Arm64, PackageKind.Script, InstallScript: "evil"), null, null)).Ok);
    }

    [DbFact]
    public async Task A_version_carries_an_installer_per_platform_and_the_agent_takes_its_own()
    {
        await using var t = await TestDatabase.CreateAsync();
        var package = (await PackageService.SaveAsync(t.Db, Admin, null, new PackageForm("Viewer", "Contoso", null, null, DetectionKind.DisplayName, "Viewer", false))).Value!;
        var v = (await PackageVersions.CreateAsync(t.Db, Admin, package.Id, "3.1", "upload")).Value!;
        foreach (var (arch, script) in new[] { (InstallerArchitecture.X64, "x64"), (InstallerArchitecture.X86, "x86"), (InstallerArchitecture.Arm64, "arm64") })
        {
            Assert.True((await PackageVersions.AddInstallerAsync(t.Db, Admin, null!, v.Id, new InstallerForm(arch, PackageKind.Script, InstallScript: "Write-Output " + script), null, null)).Ok);
        }

        Assert.False((await PackageVersions.AddInstallerAsync(t.Db, Admin, null!, v.Id, new InstallerForm(InstallerArchitecture.X64, PackageKind.Script, InstallScript: "again"), null, null)).Ok);
        var version = await t.Db.PackageVersions.AsNoTracking().Include(x => x.Installers).SingleAsync();
        var job = PackageVersions.Template(version, uninstall: false)!;
        Assert.Equal("Write-Output x64", job.Script);
        Assert.Equal(3, job.Alternatives!.Count);
        Assert.Equal("Write-Output arm64", Architectures.Choose(job, Architectures.Arm64)!.Script);
        Assert.Equal("Write-Output x86", Architectures.Choose(job, Architectures.X86)!.Script);

        // only x86 there: an x64 and an arm64 computer take it too; an x64 installer does not fit an x86 computer
        var only86 = job with { Alternatives = [job.Alternatives![1]] };
        Assert.Equal("Write-Output x86", Architectures.Choose(only86, Architectures.Arm64)!.Script);
        Assert.Null(Architectures.Choose(job with { Alternatives = [job.Alternatives[0]] }, Architectures.X86));
    }
}
