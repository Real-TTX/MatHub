using MatHub.Data;
using MatHub.Services.Access;
using Microsoft.AspNetCore.Antiforgery;

namespace MatHub.Services.Scripts;

/// <summary>The request of the visual editors: their model and which editor it is.</summary>
public sealed record CompileRequest(string Editor, ScriptModel Model);

/// <summary>A test from the editor: the computer, a trial run or not, and what is in the editor (code, or the model as JSON).</summary>
public sealed record TestRequest(long ClientId, bool DryRun, string? Code, string? Model);

/// <summary>
/// The addresses the script editors call while a person works (<c>/live/scripts/…</c>, cookie and anti-forgery header): the preview of the PowerShell a model
/// becomes. What runs is compiled again on save.
/// </summary>
public static class ScriptEndpoints
{
    public static void MapScriptEndpoints(this WebApplication app)
    {
        app.MapPost("/live/scripts/compile", async (CompileRequest request, HttpContext http, CurrentUser me, IAntiforgery anti) =>
        {
            await anti.ValidateRequestAsync(http);
            if (!me.Actor.Can(Permission.ScriptsView))
            {
                return Results.NotFound();
            }

            var editor = string.Equals(request.Editor, "flow", StringComparison.OrdinalIgnoreCase) ? ScriptEditor.Flow : ScriptEditor.Blocks;
            var result = ScriptService.Compile(editor, request.Model ?? new ScriptModel());
            return Results.Ok(new { code = ScriptBlocks.WithoutPrelude(result.Code), errors = result.Errors });
        });

        // a test from the editor: what is in it runs on the chosen computer (remembered for the person), at once or as a trial run
        app.MapPost("/live/scripts/{id:long}/test", async (long id, TestRequest request, HttpContext http, CurrentUser me, IAntiforgery anti, AppDbContext db) =>
        {
            await anti.ValidateRequestAsync(http);
            var outcome = await ScriptTester.StartAsync(db, me.Actor, id, new ScriptTest(request.ClientId, request.DryRun, request.Code, request.Model), http.RequestAborted);
            if (outcome.NotAllowed)
            {
                return Results.NotFound();
            }

            return outcome.Ok ? Results.Ok(View(outcome.Value!)) : Results.BadRequest(new { error = outcome.Error });
        });

        // a file for the script, from the field of an action
        app.MapPost("/live/scripts/{id:long}/files", async (long id, HttpContext http, CurrentUser me, IAntiforgery anti, AppDbContext db, ScriptFileStore store) =>
        {
            await anti.ValidateRequestAsync(http);
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (form.Files.Count == 0)
            {
                return Results.BadRequest(new { error = Localization.Text.T("ScriptFiles.Error.None") });
            }

            var file = form.Files[0];
            await using var stream = file.OpenReadStream();
            var outcome = await ScriptFileService.UploadAsync(db, me.Actor, store, id, file.FileName, stream, http.RequestAborted);
            return outcome.NotAllowed ? Results.NotFound() : outcome.Ok ? Results.Ok(new { name = outcome.Value!.Name }) : Results.BadRequest(new { error = outcome.Error });
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());

        app.MapGet("/live/runs/{runId:long}", async (long runId, CurrentUser me, AppDbContext db, HttpContext http) =>
            await ScriptTester.StateAsync(db, me.Actor, runId, http.RequestAborted) is { } state ? Results.Ok(View(state)) : Results.NotFound());
    }

    private static object View(TestState s) => new
    {
        runId = s.RunId,
        jobId = s.JobId,
        status = s.Status.ToString().ToLowerInvariant(),
        statusText = Localization.Text.T(JobUi.StatusKey(s.Status)),
        tone = JobUi.Tone(s.Status),
        done = s.Status is RunStatus.Succeeded or RunStatus.Failed or RunStatus.Cancelled,
        exitCode = s.ExitCode,
        output = s.Output,
    };
}
