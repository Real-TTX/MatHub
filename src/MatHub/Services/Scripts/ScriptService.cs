using MatHub.Data;
using MatHub.Services.Access;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services.Scripts;

public sealed record ScriptRow(long Id, string Name, string? Category, ScriptLanguage Language, ScriptEditor Editor, DateTime Updated, int Runs);

public sealed record ScriptForm(string Name, string? Description, string? Category, ScriptLanguage Language, ScriptEditor Editor, int TimeoutSeconds);

/// <summary>
/// The scripts (static business layer). A script is written as code, or put together from blocks or as a flow: then MatHub keeps the model and compiles it to
/// PowerShell on every save (<see cref="ScriptBlocks"/>), and the compiled code is what runs.
/// </summary>
public static class ScriptService
{
    public static async Task<IReadOnlyList<ScriptRow>?> ListAsync(AppDbContext db, Actor actor, string? search, string? category, string? editor, string sort, bool desc, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ScriptsView))
        {
            return null;
        }

        var q = db.Scripts.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = $"%{search.Trim()}%";
            q = q.Where(x => EF.Functions.ILike(x.Name, s) || EF.Functions.ILike(x.Description ?? string.Empty, s) || EF.Functions.ILike(x.Content, s));
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            q = q.Where(x => x.Category == category);
        }

        if (Enum.TryParse<ScriptEditor>(editor, true, out var e))
        {
            q = q.Where(x => x.Editor == e);
        }

        q = (sort, desc) switch
        {
            ("updated", false) => q.OrderBy(x => x.UpdateDate),
            ("updated", true) => q.OrderByDescending(x => x.UpdateDate),
            ("category", false) => q.OrderBy(x => x.Category).ThenBy(x => x.Name),
            ("category", true) => q.OrderByDescending(x => x.Category).ThenBy(x => x.Name),
            (_, true) => q.OrderByDescending(x => x.Name),
            _ => q.OrderBy(x => x.Name),
        };
        return await q.Select(x => new ScriptRow(x.Id, x.Name, x.Category, x.Language, x.Editor, x.UpdateDate, db.Jobs.Count(j => j.ScriptId == x.Id))).ToListAsync(ct);
    }

    public static async Task<IReadOnlyList<string>> CategoriesAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
        => actor.Can(Permission.ScriptsView)
            ? await db.Scripts.Where(s => s.Category != null).Select(s => s.Category!).Distinct().OrderBy(c => c).ToListAsync(ct)
            : [];

    public static async Task<Script?> GetAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
        => actor.Can(Permission.ScriptsView) ? await db.Scripts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) : null;

    public static async Task<IReadOnlyList<(long Id, string Name)>> ChoicesAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ScriptsView))
        {
            return [];
        }

        var rows = await db.Scripts.AsNoTracking().OrderBy(s => s.Name).Select(s => new { s.Id, s.Name }).ToListAsync(ct);
        return rows.Select(r => (r.Id, r.Name)).ToList();
    }

    /// <summary>Saves name, description, language, editor and time. A script that moves from a visual editor to code keeps the compiled code to go on with.</summary>
    public static async Task<Outcome<Script>> SaveAsync(AppDbContext db, Actor actor, long? id, ScriptForm form, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ScriptsEdit))
        {
            return Outcome<Script>.Denied();
        }

        var name = form.Name.Trim();
        if (name.Length == 0)
        {
            return Outcome<Script>.Fail(Localization.Text.T("Common.NameRequired"));
        }

        if (form.TimeoutSeconds is < 10 or > 86400)
        {
            return Outcome<Script>.Fail(Localization.Text.T("Scripts.Timeout.Range"));
        }

        Script script;
        if (id is { } existing)
        {
            var found = await db.Scripts.FirstOrDefaultAsync(s => s.Id == existing, ct);
            if (found is null)
            {
                return Outcome<Script>.Denied();
            }

            script = found;
        }
        else
        {
            script = new Script();
            db.Scripts.Add(script);
        }

        script.Name = name.Length > 120 ? name[..120] : name;
        script.Description = Cut(form.Description, 1000);
        script.Category = Cut(form.Category, 60);
        script.Editor = form.Editor;

        // the visual editors write PowerShell
        script.Language = form.Editor == ScriptEditor.Code ? form.Language : ScriptLanguage.PowerShell;
        if (form.Editor == ScriptEditor.Code)
        {
            script.Model = null;
        }
        else if (script.Model is null && form.Editor == ScriptEditor.Flow)
        {
            script.Model = ScriptBlocks.Serialize(new ScriptModel
            {
                Nodes = [new FlowNode { Id = "start", Type = "start", X = 60, Y = 60 }, new FlowNode { Id = "end", Type = "end", X = 60, Y = 260 }],
                Edges = [new FlowEdge { From = "start", To = "end" }],
            });
        }

        script.TimeoutSeconds = form.TimeoutSeconds;
        ActivityLog.Add(db, actor, "script", id is null ? "create" : "update", script.Name);
        await db.SaveChangesAsync(ct);
        return Outcome<Script>.Success(script);
    }

    /// <summary>
    /// Saves what the editor of the script made: the code (code editor), or the model of blocks or flow, which is compiled here (what the browser compiled for the
    /// preview counts for nothing). A model with errors is saved with its errors; the code stays as it was until it compiles.
    /// </summary>
    public static async Task<Outcome<(Script Script, IReadOnlyList<string> Errors)>> SaveContentAsync(AppDbContext db, Actor actor, long id, string? content, string? model, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ScriptsEdit))
        {
            return Outcome<(Script, IReadOnlyList<string>)>.Denied();
        }

        var script = await db.Scripts.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (script is null)
        {
            return Outcome<(Script, IReadOnlyList<string>)>.Denied();
        }

        IReadOnlyList<string> errors = [];
        if (script.Editor == ScriptEditor.Code)
        {
            script.Content = (content ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        else
        {
            var parsed = ScriptBlocks.Parse(model);
            var compiled = Compile(script.Editor, parsed);
            errors = compiled.Errors;
            script.Model = ScriptBlocks.Serialize(parsed);
            if (compiled.Ok)
            {
                script.Content = compiled.Code;
            }
        }

        ActivityLog.Add(db, actor, "script", "edit", script.Name);
        await db.SaveChangesAsync(ct);
        return Outcome<(Script, IReadOnlyList<string>)>.Success((script, errors));
    }

    public static CompileResult Compile(ScriptEditor editor, ScriptModel model)
        => editor == ScriptEditor.Flow ? ScriptBlocks.CompileFlow(model) : ScriptBlocks.CompileBlocks(model);

    /// <summary>A copy of a script ("Name (copy)").</summary>
    public static async Task<Outcome<Script>> DuplicateAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ScriptsEdit))
        {
            return Outcome<Script>.Denied();
        }

        var source = await db.Scripts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (source is null)
        {
            return Outcome<Script>.Denied();
        }

        var name = Localization.Text.T("Scripts.CopyOf", source.Name);
        var copy = new Script
        {
            Name = name.Length > 120 ? name[..120] : name, Description = source.Description, Category = source.Category, Language = source.Language,
            Editor = source.Editor, Content = source.Content, Model = source.Model, TimeoutSeconds = source.TimeoutSeconds,
        };
        db.Scripts.Add(copy);
        ActivityLog.Add(db, actor, "script", "create", copy.Name);
        await db.SaveChangesAsync(ct);
        return Outcome<Script>.Success(copy);
    }

    public static async Task<Outcome<string>> DeleteAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ScriptsEdit))
        {
            return Outcome<string>.Denied();
        }

        var script = await db.Scripts.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (script is null)
        {
            return Outcome<string>.Denied();
        }

        db.Scripts.Remove(script);
        ActivityLog.Add(db, actor, "script", "delete", script.Name);
        await db.SaveChangesAsync(ct);
        return Outcome<string>.Success(script.Name);
    }

    private static string? Cut(string? text, int max)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > max ? t[..max] : t;
    }
}
