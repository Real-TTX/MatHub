using MatHub.Data;
using MatHub.Protocol;
using MatHub.Services.Access;
using MatHub.Services.Agent;
using MatHub.Services.Clients;
using MatHub.Services.Deployments;
using MatHub.Services.Jobs;
using MatHub.Services.Packages;
using MatHub.Services.Scripts;
using Microsoft.EntityFrameworkCore;

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

        // a reinstall on the same computer finds its client again, with a new secret
        var again = await EnrollAsync(t, key, "PC-1", "machine-1");
        Assert.Equal(client.Id, again.Id);
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

    [DbFact]
    public async Task The_planner_installs_what_is_missing_once_and_updates_by_itself()
    {
        await using var t = await TestDatabase.CreateAsync();
        var group = (await GroupService.SaveAsync(t.Db, Admin, null, new GroupForm("All", null, null, GroupKind.Dynamic, GroupRuleField.Name, GroupRuleOperator.StartsWith, "PC-"))).Value!;
        var key = await KeyAsync(t);
        var first = await EnrollAsync(t, key, "PC-A", "m-a");
        var second = await EnrollAsync(t, key, "PC-B", "m-b");
        foreach (var c in new[] { first, second })
        {
            await InventoryService.ApplyAsync(t.Db, await t.Db.Clients.SingleAsync(x => x.Id == c.Id), new InventoryReport
            {
                HostName = c.Name,
                WingetInstalled = c.Id == second.Id ? [new WingetPackageInfo("Mozilla Firefox", "Mozilla.Firefox", "128.0")] : [],
                WingetUpgrades = c.Id == second.Id ? [new WingetUpgradeInfo("Mozilla Firefox", "Mozilla.Firefox", "128.0", "129.0")] : [],
            });
        }

        var package = (await PackageService.SaveAsync(t.Db, Admin, null, new PackageForm("Firefox", "Mozilla", null, PackageKind.Winget, null, "Mozilla.Firefox", null, null, null, DetectionKind.WingetId, null, true))).Value!;
        Assert.True((await PackageService.AssignAsync(t.Db, Admin, package.Id, group.Id, AssignmentAction.Install)).Ok);

        var plan = await Planner.RunOnceAsync(t.Db);
        Assert.Equal(2, plan.Jobs);
        var jobs = await t.Db.Jobs.AsNoTracking().Include(j => j.Runs).ToListAsync();
        Assert.Equal(first.Id, Assert.Single(jobs.Single(j => j.Kind == JobKind.PackageInstall).Runs).ClientId);
        Assert.Equal(second.Id, Assert.Single(jobs.Single(j => j.Kind == JobKind.WingetUpgrade).Runs).ClientId);

        // while the runs are open nothing is planned twice
        Assert.Equal(0, (await Planner.RunOnceAsync(t.Db)).Jobs);

        // the updates page sees the update of the second client
        var updates = await UpdateService.ListAsync(t.Db, Admin, null, null);
        Assert.Equal("Mozilla.Firefox", Assert.Single(updates!.Items).WingetId);
    }
}
