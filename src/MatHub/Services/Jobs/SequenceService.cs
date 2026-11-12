using System.Globalization;
using System.Text.Json;
using MatHub.Data;
using MatHub.Protocol;
using MatHub.Services.Access;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services.Jobs;

/// <summary>A step of a sequence as it is kept: what it does and with what (a script, a package, a winget id, seconds), and whether an error stops the rest.</summary>
public sealed record SequenceStep
{
    public SequenceStepKind Kind { get; init; } = SequenceStepKind.Script;

    public long? ScriptId { get; init; }

    public long? PackageId { get; init; }

    /// <summary>winget upgrade: one id, or every package when it is empty.</summary>
    public string? WingetId { get; init; }

    /// <summary>Wait: the seconds.</summary>
    public int? Seconds { get; init; }

    public bool ContinueOnError { get; init; }
}

/// <summary>A step of a job of a sequence: its name, what the agent runs (a copy taken when the job was made) and what comes after it.</summary>
public sealed record JobStep
{
    public string Name { get; init; } = string.Empty;

    public SequenceStepKind Kind { get; init; }

    public AgentJob Template { get; init; } = new();

    public bool ContinueOnError { get; init; }

    /// <summary>The installer of this package may be downloaded while the step runs.</summary>
    public long? PackageId { get; init; }
}

public sealed record SequenceRow(long Id, string Name, string? Description, string? Category, int Steps, DateTime Updated, int Runs);

public sealed record SequenceForm(string Name, string? Description, string? Category, IReadOnlyList<SequenceStep> Steps);

/// <summary>A step for a page: its kind, what it uses (by name) and its options.</summary>
public sealed record SequenceStepView(int Number, SequenceStep Step, string Title, string? Detail, string Icon);

/// <summary>
/// The sequences (static business layer): scripts, packages, restarts and waits one after the other, as one job. MatHub hands the agent one step at a time; when a
/// step is done the next one is queued (after a restart only when the computer started again), an error ends the run unless the step may fail. So a computer is
/// set up in one go, a migration done in steps, and every step is in the output of the run.
/// </summary>
public static class SequenceService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public const int MaxSteps = 50;

    public static IReadOnlyList<SequenceStep> StepsOf(Sequence sequence) => Parse<List<SequenceStep>>(sequence.Steps) ?? [];

    public static IReadOnlyList<JobStep> StepsOf(Job job) => Parse<List<JobStep>>(job.Steps) ?? [];

    private static T? Parse<T>(string? json)
        where T : class
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static string Icon(SequenceStepKind kind) => kind switch
    {
        SequenceStepKind.Script => "terminal",
        SequenceStepKind.PackageInstall => "download",
        SequenceStepKind.PackageUninstall => "trash",
        SequenceStepKind.Restart => "power",
        SequenceStepKind.Inventory => "list-checks",
        SequenceStepKind.Wait => "clock",
        _ => "refresh",
    };

    public static async Task<IReadOnlyList<SequenceRow>?> ListAsync(AppDbContext db, Actor actor, string? search, string? category, string sort, bool desc, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ScriptsView))
        {
            return null;
        }

        var q = db.Sequences.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = $"%{search.Trim()}%";
            q = q.Where(x => EF.Functions.ILike(x.Name, s) || EF.Functions.ILike(x.Description ?? string.Empty, s));
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            q = q.Where(x => x.Category == category);
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
        var rows = await q.Select(x => new { x.Id, x.Name, x.Description, x.Category, x.Steps, x.UpdateDate, Runs = db.Jobs.Count(j => j.SequenceId == x.Id) }).ToListAsync(ct);
        return rows.Select(r => new SequenceRow(r.Id, r.Name, r.Description, r.Category, (Parse<List<SequenceStep>>(r.Steps) ?? []).Count, r.UpdateDate, r.Runs)).ToList();
    }

    public static async Task<IReadOnlyList<string>> CategoriesAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
        => actor.Can(Permission.ScriptsView)
            ? await db.Sequences.Where(s => s.Category != null).Select(s => s.Category!).Distinct().OrderBy(c => c).ToListAsync(ct)
            : [];

    public static async Task<Sequence?> GetAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
        => actor.Can(Permission.ScriptsView) ? await db.Sequences.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) : null;

    public static async Task<IReadOnlyList<(long Id, string Name)>> ChoicesAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ScriptsView))
        {
            return [];
        }

        var rows = await db.Sequences.AsNoTracking().OrderBy(s => s.Name).Select(s => new { s.Id, s.Name }).ToListAsync(ct);
        return rows.Select(r => (r.Id, r.Name)).ToList();
    }

    /// <summary>The steps with the names of what they use, for the pages.</summary>
    public static async Task<IReadOnlyList<SequenceStepView>> DescribeAsync(AppDbContext db, IReadOnlyList<SequenceStep> steps, CancellationToken ct = default)
    {
        var scriptIds = steps.Where(s => s.ScriptId is not null).Select(s => s.ScriptId!.Value).Distinct().ToList();
        var packageIds = steps.Where(s => s.PackageId is not null).Select(s => s.PackageId!.Value).Distinct().ToList();
        var scripts = await db.Scripts.AsNoTracking().Where(s => scriptIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var packages = await db.Packages.AsNoTracking().Where(p => packageIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var missing = Localization.Text.T("Sequences.Step.Missing");
        return steps.Select((s, i) =>
        {
            var detail = s.Kind switch
            {
                SequenceStepKind.Script => s.ScriptId is { } sid && scripts.TryGetValue(sid, out var sn) ? sn : missing,
                SequenceStepKind.PackageInstall or SequenceStepKind.PackageUninstall => s.PackageId is { } pid && packages.TryGetValue(pid, out var pn) ? pn : missing,
                SequenceStepKind.Wait => Localization.Text.T("Sequences.Step.Seconds", s.Seconds ?? 60),
                SequenceStepKind.WingetUpgrade => string.IsNullOrWhiteSpace(s.WingetId) ? Localization.Text.T("Sequences.Step.AllPackages") : s.WingetId,
                _ => null,
            };
            return new SequenceStepView(i + 1, s, Localization.Text.T("Sequences.Kind." + s.Kind), detail, Icon(s.Kind));
        }).ToList();
    }

    public static async Task<Outcome<Sequence>> SaveAsync(AppDbContext db, Actor actor, long? id, SequenceForm form, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ScriptsEdit))
        {
            return Outcome<Sequence>.Denied();
        }

        var name = form.Name.Trim();
        if (name.Length == 0)
        {
            return Outcome<Sequence>.Fail(Localization.Text.T("Common.NameRequired"));
        }

        if (form.Steps.Count > MaxSteps)
        {
            return Outcome<Sequence>.Fail(Localization.Text.T("Sequences.Error.TooMany", MaxSteps));
        }

        var steps = new List<SequenceStep>();
        foreach (var (step, index) in form.Steps.Select((s, i) => (s, i + 1)))
        {
            var clean = step.Kind switch
            {
                SequenceStepKind.Script => step with { PackageId = null, WingetId = null, Seconds = null },
                SequenceStepKind.PackageInstall or SequenceStepKind.PackageUninstall => step with { ScriptId = null, WingetId = null, Seconds = null },
                SequenceStepKind.Wait => step with { ScriptId = null, PackageId = null, WingetId = null, Seconds = Math.Clamp(step.Seconds ?? 60, 1, 86400) },
                SequenceStepKind.WingetUpgrade => step with { ScriptId = null, PackageId = null, Seconds = null, WingetId = string.IsNullOrWhiteSpace(step.WingetId) ? null : step.WingetId.Trim() },
                _ => step with { ScriptId = null, PackageId = null, WingetId = null, Seconds = null },
            };
            if (clean.Kind == SequenceStepKind.Script && (clean.ScriptId is not { } sid || !await db.Scripts.AnyAsync(s => s.Id == sid, ct)))
            {
                return Outcome<Sequence>.Fail(Localization.Text.T("Sequences.Error.Script", index));
            }

            if (clean.Kind is SequenceStepKind.PackageInstall or SequenceStepKind.PackageUninstall && (clean.PackageId is not { } pid || !await db.Packages.AnyAsync(p => p.Id == pid, ct)))
            {
                return Outcome<Sequence>.Fail(Localization.Text.T("Sequences.Error.Package", index));
            }

            steps.Add(clean);
        }

        Sequence sequence;
        if (id is { } existing)
        {
            var found = await db.Sequences.FirstOrDefaultAsync(s => s.Id == existing, ct);
            if (found is null)
            {
                return Outcome<Sequence>.Denied();
            }

            sequence = found;
        }
        else
        {
            sequence = new Sequence();
            db.Sequences.Add(sequence);
        }

        sequence.Name = name.Length > 120 ? name[..120] : name;
        sequence.Description = Cut(form.Description, 1000);
        sequence.Category = Cut(form.Category, 60);
        sequence.Steps = Serialize(steps);
        ActivityLog.Add(db, actor, "sequence", id is null ? "create" : "update", sequence.Name, Localization.Text.T("Sequences.StepCount", steps.Count));
        await db.SaveChangesAsync(ct);
        return Outcome<Sequence>.Success(sequence);
    }

    public static async Task<Outcome<Sequence>> DuplicateAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ScriptsEdit))
        {
            return Outcome<Sequence>.Denied();
        }

        var source = await db.Sequences.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (source is null)
        {
            return Outcome<Sequence>.Denied();
        }

        var name = Localization.Text.T("Scripts.CopyOf", source.Name);
        var copy = new Sequence { Name = name.Length > 120 ? name[..120] : name, Description = source.Description, Category = source.Category, Steps = source.Steps };
        db.Sequences.Add(copy);
        ActivityLog.Add(db, actor, "sequence", "create", copy.Name);
        await db.SaveChangesAsync(ct);
        return Outcome<Sequence>.Success(copy);
    }

    public static async Task<Outcome<string>> DeleteAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ScriptsEdit))
        {
            return Outcome<string>.Denied();
        }

        var sequence = await db.Sequences.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (sequence is null)
        {
            return Outcome<string>.Denied();
        }

        db.Sequences.Remove(sequence);
        ActivityLog.Add(db, actor, "sequence", "delete", sequence.Name);
        await db.SaveChangesAsync(ct);
        return Outcome<string>.Success(sequence.Name);
    }

    /// <summary>
    /// The steps of a job of the sequence: each with a copy of what the agent runs. Scripts need scripts-run, packages and updates packages-deploy; a script
    /// without code or a package without an installer stops it with a message that names the step.
    /// </summary>
    public static async Task<Outcome<List<JobStep>>> BuildAsync(AppDbContext db, Actor actor, Sequence sequence, CancellationToken ct = default)
    {
        var steps = StepsOf(sequence);
        if (steps.Count == 0)
        {
            return Outcome<List<JobStep>>.Fail(Localization.Text.T("Sequences.Error.Empty"));
        }

        var result = new List<JobStep>();
        foreach (var (step, number) in steps.Select((s, i) => (s, i + 1)))
        {
            switch (step.Kind)
            {
                case SequenceStepKind.Script:
                    if (!actor.Can(Permission.ScriptsRun))
                    {
                        return Outcome<List<JobStep>>.Denied();
                    }

                    var script = await db.Scripts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == step.ScriptId, ct);
                    if (script is null || string.IsNullOrWhiteSpace(script.Content))
                    {
                        return Outcome<List<JobStep>>.Fail(Localization.Text.T("Sequences.Error.ScriptEmpty", number));
                    }

                    result.Add(new JobStep
                    {
                        Name = script.Name, Kind = step.Kind, ContinueOnError = step.ContinueOnError,
                        Template = new AgentJob { Kind = JobKinds.Script, Language = JobService.LanguageOf(script.Language), Script = script.Content, TimeoutSeconds = script.TimeoutSeconds },
                    });
                    break;
                case SequenceStepKind.PackageInstall:
                case SequenceStepKind.PackageUninstall:
                    if (!actor.Can(Permission.PackagesDeploy))
                    {
                        return Outcome<List<JobStep>>.Denied();
                    }

                    var package = await db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == step.PackageId, ct);
                    var uninstall = step.Kind == SequenceStepKind.PackageUninstall;
                    if (package is null || JobService.PackageTemplate(package, uninstall) is not { } template)
                    {
                        return Outcome<List<JobStep>>.Fail(Localization.Text.T("Sequences.Error.PackageEmpty", number));
                    }

                    result.Add(new JobStep
                    {
                        Name = Localization.Text.T(uninstall ? "Jobs.Name.Uninstall" : "Jobs.Name.Install", package.Name), Kind = step.Kind,
                        ContinueOnError = step.ContinueOnError, Template = template, PackageId = package.Id,
                    });
                    break;
                case SequenceStepKind.WingetUpgrade:
                    if (!actor.Can(Permission.PackagesDeploy))
                    {
                        return Outcome<List<JobStep>>.Denied();
                    }

                    result.Add(new JobStep
                    {
                        Name = string.IsNullOrWhiteSpace(step.WingetId) ? Localization.Text.T("Jobs.Name.UpgradeAll") : Localization.Text.T("Jobs.Name.Upgrade", step.WingetId),
                        Kind = step.Kind, ContinueOnError = step.ContinueOnError,
                        Template = new AgentJob { Kind = JobKinds.WingetUpgrade, Arguments = step.WingetId, TimeoutSeconds = 3600 },
                    });
                    break;
                case SequenceStepKind.Restart:
                    result.Add(new JobStep
                    {
                        Name = Localization.Text.T("Jobs.Name.Restart"), Kind = step.Kind, ContinueOnError = step.ContinueOnError,
                        Template = new AgentJob { Kind = JobKinds.Restart, TimeoutSeconds = 120 },
                    });
                    break;
                case SequenceStepKind.Inventory:
                    result.Add(new JobStep
                    {
                        Name = Localization.Text.T("Jobs.Name.Inventory"), Kind = step.Kind, ContinueOnError = step.ContinueOnError,
                        Template = new AgentJob { Kind = JobKinds.Inventory, TimeoutSeconds = 600 },
                    });
                    break;
                case SequenceStepKind.Wait:
                    var seconds = Math.Clamp(step.Seconds ?? 60, 1, 86400);
                    result.Add(new JobStep
                    {
                        Name = Localization.Text.T("Sequences.Step.Seconds", seconds), Kind = step.Kind, ContinueOnError = step.ContinueOnError,
                        Template = new AgentJob { Kind = JobKinds.Script, Script = "Start-Sleep -Seconds " + seconds.ToString(CultureInfo.InvariantCulture), TimeoutSeconds = seconds + 60 },
                    });
                    break;
            }
        }

        return Outcome<List<JobStep>>.Success(result);
    }

    private static string? Cut(string? text, int max)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > max ? t[..max] : t;
    }
}
