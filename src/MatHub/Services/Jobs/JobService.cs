using MatHub.Services.Packages;
using System.Text.Json;
using MatHub.Data;
using MatHub.Protocol;
using MatHub.Services.Access;
using MatHub.Services.Scripts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services.Jobs;

/// <summary>What a person asks for: run something on clients and on the members of groups.</summary>
public sealed record JobRequest(JobKind Kind, long? ScriptId, long? PackageId, string? WingetId, IReadOnlyList<long> ClientIds, IReadOnlyList<long> GroupIds, DateTime? NotBefore = null, long? SequenceId = null);

/// <summary>A job in a list; <paramref name="NotBefore"/>: it starts then (planned), null: at once.</summary>
public sealed record JobRow(long Id, string Name, JobKind Kind, string Origin, DateTime Created, string? CreatedBy, int Total, int Queued, int Running, int Succeeded, int Failed, int Cancelled, DateTime? NotBefore = null)
{
    /// <summary>Waits for its time: nothing ran yet and its start is still to come.</summary>
    public bool Planned => NotBefore is { } start && start > DateTime.UtcNow && Running == 0 && Queued > 0;
}

public sealed record JobCriteria(string? Search, string? State, string? Kind, long? ClientId, int Page, int PageSize);

public sealed record JobList(IReadOnlyList<JobRow> Items, int Total, int Matching);

/// <param name="Step">A sequence: the step that runs or comes next (0 = the first).</param>
public sealed record RunRow(long Id, long ClientId, string ClientName, RunStatus Status, DateTime? Started, DateTime? Finished, int? ExitCode, string? Output, int Step = 0);

/// <param name="Steps">A sequence: its steps (else empty).</param>
public sealed record JobDetail(Job Job, string? CreatedBy, IReadOnlyList<RunRow> Runs, string? ScriptText, IReadOnlyList<JobStep> Steps);

public sealed record ClientRunRow(long Id, long JobId, string JobName, JobKind Kind, RunStatus Status, DateTime Created, DateTime? Started, DateTime? Finished, int? ExitCode);

/// <summary>
/// Jobs: work for clients. A job keeps a copy of what runs (<see cref="Job.Payload"/>: the <see cref="AgentJob"/> without its run), every client a
/// <see cref="JobRun"/>. The agent takes its queued runs with its next call (<see cref="TakeAsync"/>) and reports each one (<see cref="CompleteAsync"/>).
/// </summary>
public static class JobService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public const int MaxOutput = 64 * 1024;

    /// <summary>The permission a kind of job needs.</summary>
    public static string PermissionFor(JobKind kind) => kind switch
    {
        JobKind.Script or JobKind.Sequence => Permission.ScriptsRun,
        JobKind.PackageInstall or JobKind.PackageUninstall or JobKind.WingetUpgrade => Permission.PackagesDeploy,
        _ => Permission.ClientsManage,
    };

    public static string LanguageOf(ScriptLanguage language) => language switch
    {
        ScriptLanguage.Cmd => "cmd",
        ScriptLanguage.Bash => "bash",
        _ => "powershell",
    };

    /// <summary>The address the agent downloads the installer of a package from (relative to the server it talks to).</summary>
    public static string PackageFileUrl(long packageId) => $"/api/agent/v1/packages/{packageId}/file";

    /// <summary>What the agent runs to install or remove a package.</summary>
    public static AgentJob? PackageTemplate(Package p, bool uninstall)
    {
        return (p.Kind, uninstall) switch
        {
            (PackageKind.Winget, false) => new AgentJob { Kind = JobKinds.WingetInstall, Arguments = p.WingetId, TimeoutSeconds = 3600 },
            (PackageKind.Winget, true) => new AgentJob { Kind = JobKinds.WingetUninstall, Arguments = p.WingetId, TimeoutSeconds = 3600 },
            (PackageKind.Msi, false) when p.FileName is not null => new AgentJob
            {
                Kind = JobKinds.MsiInstall, DownloadUrl = PackageFileUrl(p.Id), Sha256 = p.FileSha256, FileName = p.FileName, Arguments = p.InstallArguments, TimeoutSeconds = 3600,
            },
            (PackageKind.Msi, true) when p.FileName is not null => new AgentJob
            {
                Kind = JobKinds.MsiUninstall, DownloadUrl = PackageFileUrl(p.Id), Sha256 = p.FileSha256, FileName = p.FileName, TimeoutSeconds = 3600,
            },
            (PackageKind.Exe, false) when p.FileName is not null => new AgentJob
            {
                Kind = JobKinds.ExeInstall, DownloadUrl = PackageFileUrl(p.Id), Sha256 = p.FileSha256, FileName = p.FileName, Arguments = p.InstallArguments, TimeoutSeconds = 3600,
            },
            (PackageKind.Exe, true) when !string.IsNullOrWhiteSpace(p.UninstallCommand) => new AgentJob { Kind = JobKinds.Command, Arguments = p.UninstallCommand, TimeoutSeconds = 3600 },
            (PackageKind.Script, false) when !string.IsNullOrWhiteSpace(p.InstallScript) => new AgentJob { Kind = JobKinds.Script, Script = p.InstallScript, TimeoutSeconds = 3600 },
            (PackageKind.Script, true) when !string.IsNullOrWhiteSpace(p.UninstallCommand) => new AgentJob { Kind = JobKinds.Script, Script = p.UninstallCommand, TimeoutSeconds = 3600 },
            _ => null,
        };
    }

    /// <summary>Makes a job for the clients and groups of the request that the actor reaches. Fails when nothing can run or no client is left.</summary>
    public static async Task<Outcome<Job>> CreateAsync(AppDbContext db, Actor actor, JobRequest request, string origin = "manual", long? assignmentId = null, CancellationToken ct = default, long? kioskItemId = null)
    {
        if (!actor.Can(PermissionFor(request.Kind)))
        {
            return Outcome<Job>.Denied();
        }

        var job = new Job { Kind = request.Kind, Origin = origin, PackageAssignmentId = assignmentId, NotBeforeDate = request.NotBefore, KioskItemId = kioskItemId };
        AgentJob template;
        switch (request.Kind)
        {
            case JobKind.Script:
                var script = request.ScriptId is { } sid ? await db.Scripts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid, ct) : null;
                if (script is null || !actor.Can(Permission.ScriptsView))
                {
                    return Outcome<Job>.Denied();
                }

                if (string.IsNullOrWhiteSpace(script.Content))
                {
                    return Outcome<Job>.Fail(Localization.Text.T("Jobs.Error.EmptyScript"));
                }

                job.Name = script.Name;
                job.ScriptId = script.Id;
                job.Language = script.Language;
                job.TimeoutSeconds = script.TimeoutSeconds;
                template = new AgentJob { Kind = JobKinds.Script, Language = LanguageOf(script.Language), Script = script.Content, TimeoutSeconds = script.TimeoutSeconds };
                break;
            case JobKind.PackageInstall:
            case JobKind.PackageUninstall:
                var package = request.PackageId is { } pid ? await db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid, ct) : null;
                if (package is null)
                {
                    return Outcome<Job>.Denied();
                }

                var uninstall = request.Kind == JobKind.PackageUninstall;
                if (PackageTemplate(package, uninstall) is not { } packageJob)
                {
                    return Outcome<Job>.Fail(Localization.Text.T(uninstall ? "Jobs.Error.NoUninstall" : "Jobs.Error.NoInstaller"));
                }

                job.Name = Localization.Text.T(uninstall ? "Jobs.Name.Uninstall" : "Jobs.Name.Install", package.Name);
                job.PackageId = package.Id;
                job.TimeoutSeconds = packageJob.TimeoutSeconds;
                template = packageJob;
                break;
            case JobKind.WingetUpgrade:
                var id = request.WingetId?.Trim();
                job.Name = string.IsNullOrEmpty(id) ? Localization.Text.T("Jobs.Name.UpgradeAll") : Localization.Text.T("Jobs.Name.Upgrade", await ProgramNameAsync(db, id, ct));
                job.TimeoutSeconds = 3600;
                template = new AgentJob { Kind = JobKinds.WingetUpgrade, Arguments = string.IsNullOrEmpty(id) ? null : id, TimeoutSeconds = 3600 };
                break;
            case JobKind.Inventory:
                job.Name = Localization.Text.T("Jobs.Name.Inventory");
                template = new AgentJob { Kind = JobKinds.Inventory, TimeoutSeconds = 600 };
                break;
            case JobKind.Restart:
                job.Name = Localization.Text.T("Jobs.Name.Restart");
                template = new AgentJob { Kind = JobKinds.Restart, TimeoutSeconds = 120 };
                break;
            case JobKind.Sequence:
                var sequence = request.SequenceId is { } qid ? await db.Sequences.AsNoTracking().FirstOrDefaultAsync(q => q.Id == qid, ct) : null;
                if (sequence is null || !actor.Can(Permission.ScriptsView))
                {
                    return Outcome<Job>.Denied();
                }

                var built = await SequenceService.BuildAsync(db, actor, sequence, ct);
                if (!built.Ok)
                {
                    return built.NotAllowed ? Outcome<Job>.Denied() : Outcome<Job>.Fail(built.Error!);
                }

                job.Name = sequence.Name;
                job.SequenceId = sequence.Id;
                job.Steps = SequenceService.Serialize(built.Value!);
                job.TimeoutSeconds = built.Value!.Max(x => x.Template.TimeoutSeconds);
                template = built.Value![0].Template;
                break;
            default:
                return Outcome<Job>.Denied();
        }

        job.Payload = JsonSerializer.Serialize(template, Json);
        var clients = await TargetsAsync(db, actor, request.ClientIds, request.GroupIds, ct);
        if (clients.Count == 0)
        {
            return Outcome<Job>.Fail(Localization.Text.T("Jobs.Error.NoTargets"));
        }

        foreach (var clientId in clients)
        {
            job.Runs.Add(new JobRun { ClientId = clientId });
        }

        db.Jobs.Add(job);
        if (origin == "manual")
        {
            ActivityLog.Add(db, actor, "job", "run", job.Name, Ui.ClientCount(clients.Count));
        }

        await db.SaveChangesAsync(ct);
        return Outcome<Job>.Success(job);
    }

    /// <summary>The active clients of the request the actor reaches: those named and the members of the groups.</summary>
    /// <summary>The name of a winget program as people know it: its package, else what Windows calls it on a client, else the id.</summary>
    private static async Task<string> ProgramNameAsync(AppDbContext db, string wingetId, CancellationToken ct)
    {
        var lower = wingetId.ToLowerInvariant();
        var package = await db.Packages.AsNoTracking().Where(p => p.WingetId != null && p.WingetId.ToLower() == lower).Select(p => p.Name).FirstOrDefaultAsync(ct);
        if (package is not null)
        {
            return package;
        }

        var installed = await db.ClientSoftware.AsNoTracking().Where(s => s.WingetId != null && s.WingetId.ToLower() == lower).Select(s => s.Name).FirstOrDefaultAsync(ct);
        return installed is null ? wingetId : PackageService.CleanName(installed);
    }

    public static async Task<List<long>> TargetsAsync(AppDbContext db, Actor actor, IReadOnlyList<long> clientIds, IReadOnlyList<long> groupIds, CancellationToken ct = default)
    {
        var ids = clientIds.ToList();
        var groups = groupIds.ToList();
        return await AccessRules.Clients(db, actor)
            .Where(c => c.Status == ClientStatus.Active)
            .Where(c => ids.Contains(c.Id) || db.ClientGroupMembers.Any(m => m.ClientId == c.Id && groups.Contains(m.ClientGroupId)))
            .Select(c => c.Id).ToListAsync(ct);
    }

    // ---- the list and a job -----------------------------------------------------------------------------------------------------

    /// <summary>The jobs with a run on a client the actor reaches (the counts are of those runs only).</summary>
    public static async Task<JobList?> ListAsync(AppDbContext db, Actor actor, JobCriteria c, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.JobsView))
        {
            return null;
        }

        var reachable = AccessRules.Clients(db, actor).Select(x => x.Id);
        var runs = db.JobRuns.Where(r => reachable.Contains(r.ClientId));
        var all = db.Jobs.AsNoTracking().Where(j => runs.Any(r => r.JobId == j.Id));
        var total = await all.CountAsync(ct);
        var q = all;
        if (!string.IsNullOrWhiteSpace(c.Search))
        {
            var s = $"%{c.Search.Trim()}%";
            q = q.Where(j => EF.Functions.ILike(j.Name, s));
        }

        if (c.ClientId is { } clientId)
        {
            q = q.Where(j => runs.Any(r => r.JobId == j.Id && r.ClientId == clientId));
        }

        q = c.State switch
        {
            "open" => q.Where(j => runs.Any(r => r.JobId == j.Id && (r.Status == RunStatus.Queued || r.Status == RunStatus.Running))),
            "failed" => q.Where(j => runs.Any(r => r.JobId == j.Id && r.Status == RunStatus.Failed)),
            "done" => q.Where(j => !runs.Any(r => r.JobId == j.Id && (r.Status == RunStatus.Queued || r.Status == RunStatus.Running))),
            _ => q,
        };
        q = c.Kind switch
        {
            "script" => q.Where(j => j.Kind == JobKind.Script || j.Kind == JobKind.Sequence),
            "package" => q.Where(j => j.Kind == JobKind.PackageInstall || j.Kind == JobKind.PackageUninstall || j.Kind == JobKind.WingetUpgrade),
            "system" => q.Where(j => j.Kind == JobKind.Inventory || j.Kind == JobKind.Restart),
            _ => q,
        };

        var matching = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(j => j.CreateDate).Skip((c.Page - 1) * c.PageSize).Take(c.PageSize)
            .Select(j => new
            {
                j.Id, j.Name, j.Kind, j.Origin, j.CreateDate, j.NotBeforeDate,
                By = db.Users.IgnoreQueryFilters().Where(u => u.Id == j.CreateUserId).Select(u => u.DisplayName).FirstOrDefault(),
                Counts = runs.Where(r => r.JobId == j.Id).GroupBy(r => r.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToList(),
            })
            .ToListAsync(ct);
        return new JobList(rows.Select(r =>
        {
            int N(RunStatus s) => r.Counts.FirstOrDefault(x => x.Status == s)?.Count ?? 0;
            return new JobRow(r.Id, r.Name, r.Kind, r.Origin, r.CreateDate, r.By, r.Counts.Sum(x => x.Count), N(RunStatus.Queued), N(RunStatus.Running), N(RunStatus.Succeeded), N(RunStatus.Failed), N(RunStatus.Cancelled), r.NotBeforeDate);
        }).ToList(), total, matching);
    }

    public static async Task<JobDetail?> GetAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.JobsView))
        {
            return null;
        }

        var reachable = AccessRules.Clients(db, actor).Select(x => x.Id);
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
        {
            return null;
        }

        var runs = await db.JobRuns.AsNoTracking().Where(r => r.JobId == id && reachable.Contains(r.ClientId))
            .OrderBy(r => r.Client!.Name)
            .Select(r => new RunRow(r.Id, r.ClientId, r.Client!.Name, r.Status, r.StartedDate, r.FinishedDate, r.ExitCode, r.Output, r.Step))
            .ToListAsync(ct);
        if (runs.Count == 0)
        {
            return null;
        }

        var by = job.CreateUserId is { } uid ? await db.Users.IgnoreQueryFilters().Where(u => u.Id == uid).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) : null;
        var template = Template(job);
        var steps = job.Kind == JobKind.Sequence ? SequenceService.StepsOf(job) : [];
        var text = job.Kind == JobKind.Sequence ? null : template?.Script ?? template?.Arguments;
        return new JobDetail(job, by, runs, text, steps);
    }

    /// <summary>The runs of one client, newest first (the tab of the client).</summary>
    public static async Task<IReadOnlyList<ClientRunRow>> RunsOfClientAsync(AppDbContext db, Actor actor, long clientId, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.JobsView) || !await AccessRules.ReachesAsync(db, actor, clientId, ct))
        {
            return [];
        }

        return await db.JobRuns.AsNoTracking().Where(r => r.ClientId == clientId).OrderByDescending(r => r.Id).Take(100)
            .Select(r => new ClientRunRow(r.Id, r.JobId, r.Job!.Name, r.Job.Kind, r.Status, r.Job.CreateDate, r.StartedDate, r.FinishedDate, r.ExitCode))
            .ToListAsync(ct);
    }

    /// <summary>One run with its output (the page of a run).</summary>
    public static async Task<(JobRun Run, string JobName, JobKind Kind, string ClientName)?> RunAsync(AppDbContext db, Actor actor, long runId, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.JobsView))
        {
            return null;
        }

        var reachable = AccessRules.Clients(db, actor).Select(x => x.Id);
        var row = await db.JobRuns.AsNoTracking().Where(r => r.Id == runId && reachable.Contains(r.ClientId))
            .Select(r => new { Run = r, r.Job!.Name, r.Job.Kind, Client = r.Client!.Name }).FirstOrDefaultAsync(ct);
        return row is null ? null : (row.Run, row.Name, row.Kind, row.Client);
    }

    public static AgentJob? Template(Job job)
    {
        try
        {
            return JsonSerializer.Deserialize<AgentJob>(job.Payload, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Cancels the runs of a job that have not started (on the clients the actor reaches).</summary>
    public static async Task<Outcome<int>> CancelAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.JobsManage))
        {
            return Outcome<int>.Denied();
        }

        var reachable = AccessRules.Clients(db, actor).Select(x => x.Id);
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
        {
            return Outcome<int>.Denied();
        }

        var count = await db.JobRuns.Where(r => r.JobId == id && r.Status == RunStatus.Queued && reachable.Contains(r.ClientId))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatus.Cancelled).SetProperty(r => r.FinishedDate, DateTime.UtcNow), ct);
        ActivityLog.Add(db, actor, "job", "cancel", job.Name, Ui.ClientCount(count));
        await db.SaveChangesAsync(ct);
        return Outcome<int>.Success(count);
    }

    /// <summary>Runs the job again on the clients where it failed or was cancelled (a new job with the same payload).</summary>
    public static async Task<Outcome<Job>> RetryAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        var old = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
        if (old is null || !actor.Can(PermissionFor(old.Kind)))
        {
            return Outcome<Job>.Denied();
        }

        var reachable = AccessRules.Clients(db, actor).Where(c => c.Status == ClientStatus.Active).Select(x => x.Id);
        var clients = await db.JobRuns.Where(r => r.JobId == id && (r.Status == RunStatus.Failed || r.Status == RunStatus.Cancelled) && reachable.Contains(r.ClientId))
            .Select(r => r.ClientId).ToListAsync(ct);
        if (clients.Count == 0)
        {
            return Outcome<Job>.Fail(Localization.Text.T("Jobs.Error.NothingToRetry"));
        }

        var job = new Job
        {
            Name = old.Name, Kind = old.Kind, ScriptId = old.ScriptId, PackageId = old.PackageId, Payload = old.Payload, Language = old.Language,
            TimeoutSeconds = old.TimeoutSeconds, Origin = "manual", SequenceId = old.SequenceId, Steps = old.Steps,
        };
        foreach (var c in clients)
        {
            job.Runs.Add(new JobRun { ClientId = c });
        }

        db.Jobs.Add(job);
        ActivityLog.Add(db, actor, "job", "retry", job.Name, Ui.ClientCount(clients.Count));
        await db.SaveChangesAsync(ct);
        return Outcome<Job>.Success(job);
    }

    // ---- the agent ---------------------------------------------------------------------------------------------------------------

    /// <summary>A sequence waits after a restart until the computer started again, at most this long.</summary>
    public static readonly TimeSpan RestartWait = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The queued runs of a client that are due, at most <paramref name="max"/>: they are running from now on. A sequence hands out its current step (after a
    /// restart only when the client started again). The secrets the script names go with it (<see cref="SecretService.ResolveAsync"/>).
    /// </summary>
    public static async Task<IReadOnlyList<AgentJob>> TakeAsync(AppDbContext db, long clientId, int max, CancellationToken ct = default, IDataProtectionProvider? protection = null)
    {
        var now = DateTime.UtcNow;
        var runs = await db.JobRuns.Include(r => r.Job)
            .Where(r => r.ClientId == clientId && r.Status == RunStatus.Queued && (r.Job!.NotBeforeDate == null || r.Job.NotBeforeDate <= now))
            .OrderBy(r => r.Id).Take(max).ToListAsync(ct);
        DateTime? boot = null;
        if (runs.Any(r => r.WaitBootDate is not null))
        {
            boot = await db.Clients.Where(c => c.Id == clientId).Select(c => c.BootDate).FirstOrDefaultAsync(ct);
        }

        var jobs = new List<AgentJob>();
        foreach (var run in runs)
        {
            AgentJob? template;
            if (run.Job!.Kind == JobKind.Sequence)
            {
                // after a restart: the computer has to be back first (a newer boot), or half an hour has gone by
                if (run.WaitBootDate is { } since && (boot is null || boot <= since) && now - since < RestartWait)
                {
                    continue;
                }

                var steps = SequenceService.StepsOf(run.Job);
                template = run.Step < steps.Count ? steps[run.Step].Template with { Title = run.Job.Name + " · " + steps[run.Step].Name } : null;
                run.WaitBootDate = null;
            }
            else
            {
                template = Template(run.Job) is { } t ? t with { Title = run.Job.Name } : null;
            }

            if (template is null)
            {
                run.Status = RunStatus.Failed;
                run.FinishedDate = now;
                run.Output = (run.Output ?? string.Empty) + "MatHub could not read the job.";
                continue;
            }

            if (protection is not null && template.Script is { } script && await SecretService.ResolveAsync(db, protection, script, ct) is { } secrets)
            {
                template = template with { Secrets = secrets };
            }

            run.Status = RunStatus.Running;
            run.StartedDate ??= now;
            run.StepDate = now;
            jobs.Add(template with { RunId = run.Id });
        }

        await db.SaveChangesAsync(ct);
        return jobs;
    }

    /// <summary>Whether the client may download the installer of the package now: a run of it is running (also as the current step of a sequence).</summary>
    public static async Task<bool> PackageAllowedAsync(AppDbContext db, long clientId, long packageId, CancellationToken ct = default)
    {
        if (await db.JobRuns.AnyAsync(r => r.ClientId == clientId && r.Job!.PackageId == packageId && r.Status == RunStatus.Running, ct))
        {
            return true;
        }

        var sequences = await db.JobRuns.AsNoTracking().Where(r => r.ClientId == clientId && r.Status == RunStatus.Running && r.Job!.Kind == JobKind.Sequence)
            .Select(r => new { r.Step, r.Job }).ToListAsync(ct);
        return sequences.Any(r => SequenceService.StepsOf(r.Job!) is var steps && r.Step < steps.Count && steps[r.Step].PackageId == packageId);
    }

    /// <summary>The state of a run of this client (the kiosk follows what it started).</summary>
    public static async Task<RunState?> StateAsync(AppDbContext db, long clientId, long runId, CancellationToken ct = default)
    {
        var run = await db.JobRuns.AsNoTracking().Include(r => r.Job).FirstOrDefaultAsync(r => r.Id == runId && r.ClientId == clientId, ct);
        return run is null ? null : State(run);
    }

    public static RunState State(JobRun run)
    {
        var steps = run.Job?.Kind == JobKind.Sequence ? SequenceService.StepsOf(run.Job).Count : 0;
        return new RunState
        {
            RunId = run.Id,
            Status = run.Status switch
            {
                RunStatus.Running => RunStates.Running,
                RunStatus.Succeeded => RunStates.Succeeded,
                RunStatus.Failed => RunStates.Failed,
                RunStatus.Cancelled => RunStates.Cancelled,
                _ => RunStates.Queued,
            },
            ExitCode = run.ExitCode,
            Finished = run.FinishedDate,
            Step = steps == 0 ? 0 : Math.Min(run.Step + 1, steps),
            Steps = steps,
        };
    }

    /// <summary>The result of a run as the agent reports it (only a running run of that client).</summary>
    public static async Task<bool> CompleteAsync(AppDbContext db, long clientId, long runId, RunResult result, CancellationToken ct = default)
    {
        var run = await db.JobRuns.Include(r => r.Job).FirstOrDefaultAsync(r => r.Id == runId && r.ClientId == clientId, ct);
        if (run is null || run.Status is not (RunStatus.Running or RunStatus.Queued))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        run.ExitCode = result.ExitCode;
        if (run.Job!.Kind == JobKind.Sequence)
        {
            // a sequence: the output of every step under its heading; the next step is queued, or the run ends
            var steps = SequenceService.StepsOf(run.Job);
            var step = run.Step < steps.Count ? steps[run.Step] : null;
            var outcome = result.Succeeded ? "done" : step?.ContinueOnError == true ? "failed, going on" : "failed";
            var heading = $"=== {run.Step + 1}/{steps.Count} {step?.Name} – {outcome}{(result.ExitCode is { } code ? $" (exit {code})" : string.Empty)}";
            run.Output = Tail(string.Join('\n', new[] { run.Output, heading, result.Output?.TrimEnd() }.Where(x => !string.IsNullOrEmpty(x))));
            if (step is null || (!result.Succeeded && !step.ContinueOnError))
            {
                run.Status = RunStatus.Failed;
                run.FinishedDate = now;
            }
            else if (run.Step + 1 < steps.Count)
            {
                run.Step++;
                run.Status = RunStatus.Queued;
                run.WaitBootDate = step.Kind == SequenceStepKind.Restart ? now : null;
            }
            else
            {
                run.Status = RunStatus.Succeeded;
                run.FinishedDate = now;
            }
        }
        else
        {
            run.Status = result.Succeeded ? RunStatus.Succeeded : RunStatus.Failed;
            run.FinishedDate = now;
            run.Output = Tail(result.Output);
        }

        await db.SaveChangesAsync(ct);

        // what is installed changed: the next call of the agent brings an inventory (the planner waits for it)
        if (run.Job!.Kind is JobKind.PackageInstall or JobKind.PackageUninstall or JobKind.WingetUpgrade or JobKind.Script or JobKind.Sequence)
        {
            await db.Clients.Where(c => c.Id == clientId).ExecuteUpdateAsync(s => s.SetProperty(c => c.InventoryRequested, true), ct);
        }
        return true;
    }

    /// <summary>Runs that never came back (the computer was switched off, the agent restarted) fail after their time and a quarter of an hour.</summary>
    public static async Task<int> FailLostRunsAsync(AppDbContext db, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var lost = await db.JobRuns.Include(r => r.Job)
            .Where(r => r.Status == RunStatus.Running && (r.StepDate ?? r.StartedDate) != null && (r.StepDate ?? r.StartedDate) < now.AddMinutes(-15))
            .ToListAsync(ct);
        var count = 0;
        foreach (var run in lost.Where(r => (r.StepDate ?? r.StartedDate)!.Value.AddSeconds(r.Job!.TimeoutSeconds).AddMinutes(15) < now))
        {
            run.Status = RunStatus.Failed;
            run.FinishedDate = now;
            run.Output = (run.Output ?? string.Empty) + "\nMatHub: the agent did not report this run in time.";
            count++;
        }

        await db.SaveChangesAsync(ct);
        return count;
    }

    private static string? Tail(string? output)
    {
        if (output is null)
        {
            return null;
        }

        return output.Length <= MaxOutput ? output : "…" + output[^MaxOutput..];
    }
}
