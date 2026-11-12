using MatHub.Services;
using System.Globalization;
using MatHub.Controls;
using MatHub.Data;
using MatHub.Localization;
using MatHub.Services.Access;
using MatHub.Services.Clients;
using MatHub.Services.Jobs;
using MatHub.Services.Packages;
using MatHub.Services.Scripts;
using Microsoft.AspNetCore.Mvc;

namespace MatHub.Pages.Jobs;

/// <summary>
/// Run something on clients: a script, an install or an uninstall of a package, updates (winget), an inventory, a restart. The targets are clients and groups;
/// only what the person may do and the clients they reach can be chosen (and the business layer checks it again). Other pages lead here with a choice made:
/// <c>?script=</c>, <c>?package=</c> (+ <c>&version=</c>: its version in test), <c>?client=</c> (also several), <c>?group=</c>.
/// A package is chosen with its version: the approved one, or the one in test (<see cref="JobVersionChoice"/>).
/// </summary>
public sealed class NewModel(AppDbContext db) : AppPageModel
{
    [BindProperty] public JobKind Kind { get; set; } = JobKind.Script;

    [BindProperty] public string ScriptId { get; set; } = string.Empty;

    [BindProperty] public string PackageId { get; set; } = string.Empty;

    [BindProperty] public string SequenceId { get; set; } = string.Empty;

    [BindProperty] public string WingetId { get; set; } = string.Empty;

    [BindProperty] public string Clients { get; set; } = string.Empty;

    [BindProperty] public string Groups { get; set; } = string.Empty;

    /// <summary>now or later.</summary>
    [BindProperty] public string When { get; set; } = "now";

    [BindProperty] public string StartDate { get; set; } = string.Empty;

    [BindProperty] public string StartTime { get; set; } = string.Empty;

    public List<Choice> WhenChoices { get; } = [new("now", Text.T("Jobs.When.Now"), null), new("later", Text.T("Jobs.When.Later"), null)];

    public List<Choice> Kinds { get; private set; } = [];

    public List<Choice> ScriptChoices { get; private set; } = [];

    public List<Choice> PackageChoices { get; private set; } = [];

    public List<Choice> SequenceChoices { get; private set; } = [];

    public List<Choice> ClientChoices { get; private set; } = [];

    public List<Choice> GroupChoices { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();
        if (Kinds.Count == 0)
        {
            return NotFound();
        }

        var q = Request.Query;
        Clients = string.Join(", ", q["client"].Where(v => long.TryParse(v, out _)));
        Groups = string.Join(", ", q["group"].Where(v => long.TryParse(v, out _)));
        if (long.TryParse(q["script"], out var script))
        {
            Kind = JobKind.Script;
            ScriptId = script.ToString(CultureInfo.InvariantCulture);
        }
        else if (long.TryParse(q["package"], out var package))
        {
            Kind = q["action"] == "uninstall" ? JobKind.PackageUninstall : JobKind.PackageInstall;
            // a version only when it is one of the choices (the one in test), else the approved one
            var asked = long.TryParse(q["version"], out var version) ? new JobVersionChoice(package, string.Empty, version, null, null).Value : null;
            PackageId = asked is not null && PackageChoices.Any(c => c.Value == asked) ? asked : package.ToString(CultureInfo.InvariantCulture);
        }
        else if (long.TryParse(q["sequence"], out var sequence))
        {
            Kind = JobKind.Sequence;
            SequenceId = sequence.ToString(CultureInfo.InvariantCulture);
        }
        else if (Enum.TryParse<JobKind>(q["kind"], true, out var kind))
        {
            Kind = kind;
        }

        if (Kinds.All(k => k.Value != Kind.ToString()))
        {
            Kind = Enum.Parse<JobKind>(Kinds[0].Value);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        DateTime? notBefore = null;
        if (When == "later")
        {
            var day = Dates.ParseIso(StartDate);
            var time = Dates.ParseTime(StartTime) ?? new TimeOnly(0, 0);
            if (day is null)
            {
                ModelState.AddModelError(nameof(StartDate), Text.T("Jobs.Error.NoStart"));
            }
            else
            {
                var local = DateTime.SpecifyKind(day.Value.ToDateTime(time), DateTimeKind.Unspecified);
                notBefore = TimeZoneInfo.ConvertTimeToUtc(local, Dates.Zone(Me.TimeZone));
                if (notBefore <= DateTime.UtcNow)
                {
                    ModelState.AddModelError(nameof(StartDate), Text.T("Jobs.Error.StartPast"));
                }
            }

            if (!ModelState.IsValid)
            {
                await LoadAsync();
                return Page();
            }
        }

        var (package, version) = JobVersionChoice.Parse(PackageId);
        var request = new JobRequest(
            Kind,
            long.TryParse(ScriptId, out var s) ? s : null,
            package,
            Kind == JobKind.WingetUpgrade ? WingetId : null,
            Ids(Clients),
            Ids(Groups),
            notBefore,
            long.TryParse(SequenceId, out var q) ? q : null,
            version);
        var outcome = await JobService.CreateAsync(db, Me.Actor, request, ct: HttpContext.RequestAborted);
        if (!outcome.Ok)
        {
            await LoadAsync();
            ModelState.AddModelError(nameof(Clients), outcome.NotAllowed ? Text.T("Common.NotAllowed") : outcome.Error!);
            return Page();
        }

        Flash(Text.T("Jobs.Started", outcome.Value!.Name));
        return LocalRedirect($"/jobs/{outcome.Value.Id}");
    }

    private static List<long> Ids(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(v => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0).Where(id => id > 0).Distinct().ToList();

    private async Task LoadAsync()
    {
        var actor = Me.Actor;
        var ct = HttpContext.RequestAborted;
        Kinds = Enum.GetValues<JobKind>()
            .Where(k => actor.Can(JobService.PermissionFor(k)) && k != JobKind.WingetUpgrade)
            .Select(k => new Choice(k.ToString(), Text.T("Jobs.Kind." + k), Text.T("Jobs.Kind." + k + ".Hint")))
            .ToList();
        ScriptChoices = (await ScriptService.ChoicesAsync(db, actor, ct)).Select(x => new Choice(x.Id.ToString(CultureInfo.InvariantCulture), x.Name)).ToList();
        PackageChoices = (await PackageVersions.JobChoicesAsync(db, actor, ct)).Select(x => new Choice(
            x.Value,
            x.VersionId is null ? x.Package : Text.T("Jobs.PackageVersion", x.Package, x.Version!),
            x.Status switch
            {
                VersionStatus.Approved => Text.T("Versions.ApprovedIs", PackageVersions.Show(x.Version)),
                VersionStatus.Testing => Text.T("Jobs.PackageVersion.Testing"),
                _ => Text.T("Versions.NoneApproved"),
            })).ToList();
        SequenceChoices = (await SequenceService.ChoicesAsync(db, actor, ct)).Select(x => new Choice(x.Id.ToString(CultureInfo.InvariantCulture), x.Name)).ToList();
        ClientChoices = (await ClientService.ChoicesAsync(db, actor, ct))
            .Select(x => new Choice(x.Id.ToString(CultureInfo.InvariantCulture), x.Name, Text.T(x.Online ? "Clients.State.Online" : "Clients.State.Offline"))).ToList();
        GroupChoices = (await GroupService.ChoicesAsync(db, actor, ct)).Select(x => new Choice(x.Id.ToString(CultureInfo.InvariantCulture), x.Name, null, x.Colour)).ToList();
    }
}
