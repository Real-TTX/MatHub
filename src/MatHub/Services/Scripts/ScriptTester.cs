using MatHub.Data;
using MatHub.Protocol;
using MatHub.Services.Access;
using MatHub.Services.Jobs;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services.Scripts;

/// <summary>A test from the script editor: the state of the editor (saved or not), the computer, and whether it is a trial run.</summary>
/// <param name="Code">The code editor: its text; null takes the saved code.</param>
/// <param name="Model">The visual editors: their model as JSON; null takes the saved model.</param>
public sealed record ScriptTest(long ClientId, bool DryRun, string? Code, string? Model);

/// <summary>The state of a run for the editor: what it is, what it wrote.</summary>
public sealed record TestState(long RunId, long JobId, RunStatus Status, int? ExitCode, string? Output, DateTime? Started, DateTime? Finished);

/// <summary>
/// Testing from the script editor: what is in the editor runs on one computer of the person ("my computer", which MatHub remembers for them) as a job of its own
/// (origin "test"), at once or as a trial run. A trial run changes nothing: the visual editors say what each action would do (dialogs, variables and checks run,
/// so the branches are the real ones); a script of code is read by the parser of PowerShell on the computer (its errors, the commands it would call), cmd and
/// bash only listed. Testing what is not saved yet needs the permission to edit scripts, running at all the one to run them.
/// </summary>
public static class ScriptTester
{
    public static async Task<Outcome<TestState>> StartAsync(AppDbContext db, Actor actor, long scriptId, ScriptTest test, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ScriptsRun) || !actor.Can(Permission.ScriptsView) || ((test.Code is not null || test.Model is not null) && !actor.Can(Permission.ScriptsEdit)))
        {
            return Outcome<TestState>.Denied();
        }

        var script = await db.Scripts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == scriptId, ct);
        if (script is null)
        {
            return Outcome<TestState>.Denied();
        }

        var client = await AccessRules.Clients(db, actor).AsNoTracking().FirstOrDefaultAsync(c => c.Id == test.ClientId, ct);
        if (client is null || client.Status != ClientStatus.Active)
        {
            return Outcome<TestState>.Fail(Localization.Text.T("Scripts.Test.NoClient"));
        }

        // what runs: the editor's state, compiled like on a save
        string code;
        var language = script.Language;
        if (script.Editor == ScriptEditor.Code)
        {
            code = (test.Code ?? script.Content).Replace("\r\n", "\n", StringComparison.Ordinal);
            if (test.DryRun)
            {
                code = DryRunCode(language, code);
                language = ScriptLanguage.PowerShell;
            }
        }
        else
        {
            var model = ScriptBlocks.Parse(test.Model ?? script.Model);
            var compiled = script.Editor == ScriptEditor.Flow ? ScriptBlocks.CompileFlow(model, test.DryRun) : ScriptBlocks.CompileBlocks(model, test.DryRun);
            if (!compiled.Ok)
            {
                return Outcome<TestState>.Fail(string.Join(" ", compiled.Errors));
            }

            code = compiled.Code;
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return Outcome<TestState>.Fail(Localization.Text.T("Jobs.Error.EmptyScript"));
        }

        var template = new AgentJob { Kind = JobKinds.Script, Language = JobService.LanguageOf(language), Script = code, TimeoutSeconds = test.DryRun ? Math.Min(script.TimeoutSeconds, 900) : script.TimeoutSeconds };
        var job = new Job
        {
            Name = Localization.Text.T(test.DryRun ? "Scripts.Test.DryName" : "Scripts.Test.Name", script.Name),
            Kind = JobKind.Script, ScriptId = script.Id, Language = language, TimeoutSeconds = template.TimeoutSeconds, Origin = "test",
            Payload = System.Text.Json.JsonSerializer.Serialize(template, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
        };
        var run = new JobRun { ClientId = client.Id };
        job.Runs.Add(run);
        db.Jobs.Add(job);

        // the computer is remembered: the next test goes there again
        if (actor.UserId is { } userId)
        {
            await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.TestClientId, client.Id), ct);
        }

        ActivityLog.Add(db, actor, "script", test.DryRun ? "trial" : "test", script.Name, client.Name, client.Id);
        await db.SaveChangesAsync(ct);
        return Outcome<TestState>.Success(new TestState(run.Id, job.Id, run.Status, null, null, null, null));
    }

    /// <summary>The state of a test run (or any run the person reaches).</summary>
    public static async Task<TestState?> StateAsync(AppDbContext db, Actor actor, long runId, CancellationToken ct = default)
    {
        var found = await JobService.RunAsync(db, actor, runId, ct);
        return found is { } r ? new TestState(r.Run.Id, r.Run.JobId, r.Run.Status, r.Run.ExitCode, r.Run.Output, r.Run.StartedDate, r.Run.FinishedDate) : null;
    }

    /// <summary>The computer the person tests on (null: none chosen yet, or out of reach now).</summary>
    public static async Task<long?> TestClientAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        if (actor.UserId is not { } userId)
        {
            return null;
        }

        var id = await db.Users.Where(u => u.Id == userId).Select(u => u.TestClientId).FirstOrDefaultAsync(ct);
        return id is { } clientId && await AccessRules.ReachesAsync(db, actor, clientId, ct) ? clientId : null;
    }

    /// <summary>
    /// A trial run of code: PowerShell is read by its parser on the computer (the errors, every command it would call, with its line), cmd and bash are listed line
    /// by line. Nothing of the script runs.
    /// </summary>
    public static string DryRunCode(ScriptLanguage language, string code)
    {
        var prefix = ScriptBlocks.Quote(Localization.Text.T("Scripts.Test.DryPrefix") + " ");
        var header = $"Write-Output {ScriptBlocks.Quote(Localization.Text.T("Scripts.Test.DryHeaderCode"))}\n";
        if (language == ScriptLanguage.PowerShell)
        {
            return header + $$"""
                $code = {{ScriptBlocks.Quote(code)}}
                $tokens = $null; $errors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseInput($code, [ref]$tokens, [ref]$errors)
                foreach ($e in $errors) { Write-Output ({{ScriptBlocks.Quote(Localization.Text.T("Scripts.Test.DryError") + " ")}} + $e.Extent.StartLineNumber + ': ' + $e.Message) }
                $commands = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true)
                foreach ($c in $commands) { Write-Output ({{prefix}} + {{ScriptBlocks.Quote(Localization.Text.T("Scripts.Test.DryLine") + " ")}} + $c.Extent.StartLineNumber + ': ' + (($c.Extent.Text -split "`n")[0]).Trim()) }
                Write-Output ({{ScriptBlocks.Quote(Localization.Text.T("Scripts.Test.DryCount") + " ")}} + @($commands).Count)
                if ($errors.Count -gt 0) { exit 1 }
                """;
        }

        var lines = code.Split('\n').Select((l, i) => (Text: l.Trim(), Number: i + 1))
            .Where(l => l.Text.Length > 0 && !l.Text.StartsWith('#') && !l.Text.StartsWith("::", StringComparison.Ordinal)
                        && !l.Text.StartsWith("rem ", StringComparison.OrdinalIgnoreCase) && !l.Text.StartsWith("@echo off", StringComparison.OrdinalIgnoreCase))
            .Select(l => $"Write-Output ({prefix} + {ScriptBlocks.Quote(Localization.Text.T("Scripts.Test.DryLine") + " " + l.Number + ": " + l.Text)})");
        return header + string.Join('\n', lines) + "\n";
    }
}
