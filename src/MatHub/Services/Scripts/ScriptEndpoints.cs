using MatHub.Data;
using MatHub.Services.Access;
using Microsoft.AspNetCore.Antiforgery;

namespace MatHub.Services.Scripts;

/// <summary>The request of the visual editors: their model and which editor it is.</summary>
public sealed record CompileRequest(string Editor, ScriptModel Model);

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
            return Results.Ok(new { code = result.Code, errors = result.Errors });
        });
    }
}
