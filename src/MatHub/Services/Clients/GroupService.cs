using MatHub.Data;
using MatHub.Services.Access;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services.Clients;

public sealed record GroupRow(long Id, string Name, string? Description, string? Colour, GroupKind Kind, int Members, string? Rule);

/// <summary>One condition of a dynamic group: a fact of the inventory, how it is compared, the value.</summary>
public sealed record GroupCondition(GroupRuleField Field, GroupRuleOperator Operator, string Value);

/// <summary>What the edit page of a group sends; <paramref name="Conditions"/> and <paramref name="MatchAny"/> only matter for a dynamic group.</summary>
public sealed record GroupForm(string Name, string? Description, string? Colour, GroupKind Kind, IReadOnlyList<GroupCondition> Conditions, bool MatchAny = false)
{
    /// <summary>A group with one condition (the tests and the simple cases).</summary>
    public GroupForm(string name, string? description, string? colour, GroupKind kind, GroupRuleField field, GroupRuleOperator op, string? value)
        : this(name, description, colour, kind, string.IsNullOrWhiteSpace(value) ? [] : [new GroupCondition(field, op, value)])
    {
    }
}

/// <summary>
/// The groups of clients (static, by hand; dynamic, by a rule over the inventory). The members of a dynamic group are computed and kept in
/// <see cref="ClientGroupMember"/> like the static ones, so every query of a target or a permission is one join: after a save of the group, after an
/// inventory of a client and every few minutes (<see cref="RefreshAllAsync"/>).
/// </summary>
public static class GroupService
{
    /// <summary>The most conditions a group has.</summary>
    public const int MaxConditions = 10;

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>The conditions of a group: the first one in its columns, the others in <see cref="ClientGroup.MoreRules"/>.</summary>
    public static IReadOnlyList<GroupCondition> Conditions(ClientGroup g) => Conditions(g.RuleField, g.RuleOperator, g.RuleValue, g.MoreRules);

    private static List<GroupCondition> Conditions(GroupRuleField field, GroupRuleOperator op, string? value, string? more)
    {
        var list = new List<GroupCondition>();
        if (!string.IsNullOrWhiteSpace(value))
        {
            list.Add(new GroupCondition(field, op, value));
        }

        if (!string.IsNullOrWhiteSpace(more))
        {
            try
            {
                list.AddRange((System.Text.Json.JsonSerializer.Deserialize<List<GroupCondition>>(more, Json) ?? []).Where(c => !string.IsNullOrWhiteSpace(c.Value)));
            }
            catch (System.Text.Json.JsonException)
            {
                // conditions MatHub cannot read count as none
            }
        }

        return list;
    }

    public static async Task<IReadOnlyList<GroupRow>?> ListAsync(AppDbContext db, Actor actor, string? search, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ClientsView))
        {
            return null;
        }

        var q = AccessRules.Groups(db, actor).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = $"%{search.Trim()}%";
            q = q.Where(g => EF.Functions.ILike(g.Name, s) || EF.Functions.ILike(g.Description ?? string.Empty, s));
        }

        var rows = await q.OrderBy(g => g.Name)
            .Select(g => new { g.Id, g.Name, g.Description, g.Colour, g.Kind, g.RuleField, g.RuleOperator, g.RuleValue, g.MoreRules, g.MatchAny, Members = g.Members.Count(m => m.Client!.UpdateState != SoftEntity.Deleted) })
            .ToListAsync(ct);
        return rows.Select(g => new GroupRow(g.Id, g.Name, g.Description, g.Colour, g.Kind, g.Members, g.Kind == GroupKind.Dynamic ? RuleText(Conditions(g.RuleField, g.RuleOperator, g.RuleValue, g.MoreRules), g.MatchAny) : null)).ToList();
    }

    /// <summary>The groups the actor may pick as a target or a scope: id, name, colour.</summary>
    public static async Task<IReadOnlyList<ClientGroupRef>> ChoicesAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
        => await AccessRules.Groups(db, actor).AsNoTracking().OrderBy(g => g.Name).Select(g => new ClientGroupRef(g.Id, g.Name, g.Colour, g.Kind)).ToListAsync(ct);

    public static async Task<ClientGroup?> GetAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
        => actor.Can(Permission.ClientsView) ? await AccessRules.Groups(db, actor).AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, ct) : null;

    /// <summary>The members of a group (names and whether they are online), for its page.</summary>
    public static async Task<IReadOnlyList<ClientRow>> MembersAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ClientsView))
        {
            return [];
        }

        var rows = await AccessRules.Clients(db, actor).AsNoTracking()
            .Where(c => db.ClientGroupMembers.Any(m => m.ClientId == c.Id && m.ClientGroupId == id))
            .OrderBy(c => c.Name).Take(500)
            .Select(x => new { x.Id, x.Name, x.Status, x.OperatingSystem, x.IpAddresses, x.LastUser, x.LastSeenDate, x.UpdatesAvailable, x.AgentVersion })
            .ToListAsync(ct);
        return rows.Select(x => new ClientRow(x.Id, x.Name, x.Status, ClientService.IsOnline(x.LastSeenDate), x.OperatingSystem, x.IpAddresses, x.LastUser, x.LastSeenDate, x.UpdatesAvailable, x.AgentVersion)).ToList();
    }

    public static async Task<Outcome<ClientGroup>> SaveAsync(AppDbContext db, Actor actor, long? id, GroupForm form, CancellationToken ct = default)
    {
        // a new group is outside every role's scope: only someone who reaches all clients may make one
        if (!actor.Can(Permission.GroupsManage) || (id is null && !actor.ReachesAll))
        {
            return Outcome<ClientGroup>.Denied();
        }

        var name = form.Name.Trim();
        if (name.Length == 0)
        {
            return Outcome<ClientGroup>.Fail(Localization.Text.T("Common.NameRequired"));
        }

        var conditions = form.Conditions.Where(c => !string.IsNullOrWhiteSpace(c.Value) && Enum.IsDefined(c.Field) && Enum.IsDefined(c.Operator))
            .Select(c => c with { Value = ClientService.Clean(c.Value, 200)! }).Take(MaxConditions).ToList();
        if (form.Kind == GroupKind.Dynamic && conditions.Count == 0)
        {
            return Outcome<ClientGroup>.Fail(Localization.Text.T("Groups.Rule.Required"));
        }

        ClientGroup? group;
        if (id is { } existing)
        {
            group = await AccessRules.Groups(db, actor).FirstOrDefaultAsync(g => g.Id == existing, ct);
            if (group is null)
            {
                return Outcome<ClientGroup>.Denied();
            }

            // the kind and the rules decide who is a member, and so whom a role limited to this group reaches: only someone who reaches all clients
            // changes them (else a role could widen its own reach)
            if (!actor.ReachesAll && (group.Kind != form.Kind
                || (form.Kind == GroupKind.Dynamic && (!Conditions(group).SequenceEqual(conditions) || group.MatchAny != form.MatchAny))))
            {
                return Outcome<ClientGroup>.Fail(Localization.Text.T("Groups.Error.RulesNeedAll"));
            }
        }
        else
        {
            group = new ClientGroup();
            db.ClientGroups.Add(group);
        }

        var kindChanged = group.Kind != form.Kind;
        group.Name = name.Length > 120 ? name[..120] : name;
        group.Description = ClientService.Clean(form.Description, 1000);
        group.Colour = Ui.IsColour(form.Colour) ? form.Colour!.ToLowerInvariant() : null;
        group.Kind = form.Kind;
        var dynamic = form.Kind == GroupKind.Dynamic;
        group.RuleField = dynamic ? conditions[0].Field : GroupRuleField.Name;
        group.RuleOperator = dynamic ? conditions[0].Operator : GroupRuleOperator.Contains;
        group.RuleValue = dynamic ? conditions[0].Value : null;
        group.MoreRules = dynamic && conditions.Count > 1 ? System.Text.Json.JsonSerializer.Serialize(conditions.Skip(1), Json) : null;
        group.MatchAny = dynamic && form.MatchAny;
        ActivityLog.Add(db, actor, "group", id is null ? "create" : "update", group.Name);
        await db.SaveChangesAsync(ct);

        if (group.Kind == GroupKind.Dynamic)
        {
            await RefreshAsync(db, group, ct);
        }
        else if (kindChanged && id is not null)
        {
            // a dynamic group that becomes static keeps its last members, now by hand
        }

        return Outcome<ClientGroup>.Success(group);
    }

    public static async Task<Outcome<string>> DeleteAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.GroupsManage) || !actor.ReachesAll)
        {
            return Outcome<string>.Denied();
        }

        var group = await db.ClientGroups.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group is null)
        {
            return Outcome<string>.Denied();
        }

        db.ClientGroups.Remove(group);
        await db.ClientGroupMembers.Where(m => m.ClientGroupId == id).ExecuteDeleteAsync(ct);
        await db.PackageAssignments.Where(a => a.ClientGroupId == id).ExecuteDeleteAsync(ct);
        await db.AccessRoleGroups.Where(a => a.ClientGroupId == id).ExecuteDeleteAsync(ct);
        ActivityLog.Add(db, actor, "group", "delete", group.Name);
        await db.SaveChangesAsync(ct);
        return Outcome<string>.Success(group.Name);
    }

    // ---- the members of the dynamic groups ----------------------------------------------------------------------------------------

    private sealed record ClientFacts(long Id, string Name, string? OperatingSystem, string? Domain, string? Manufacturer, string? Model, string? IpAddresses, string Tags);

    /// <summary>Whether a client matches the rule of a dynamic group (case-insensitive; a tag must match one tag, an address one address).</summary>
    public static bool Matches(GroupRuleField field, GroupRuleOperator op, string? value, string name, string? os, string? domain, string? manufacturer, string? model, string? ips, string tags)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        bool Test(string? candidate)
        {
            if (candidate is null)
            {
                return false;
            }

            return op switch
            {
                GroupRuleOperator.Equals => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase),
                GroupRuleOperator.StartsWith => candidate.StartsWith(value, StringComparison.OrdinalIgnoreCase),
                GroupRuleOperator.EndsWith => candidate.EndsWith(value, StringComparison.OrdinalIgnoreCase),
                _ => candidate.Contains(value, StringComparison.OrdinalIgnoreCase),
            };
        }

        static IEnumerable<string> Split(string? list) => (list ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return field switch
        {
            GroupRuleField.OperatingSystem => Test(os),
            GroupRuleField.Domain => Test(domain),
            GroupRuleField.Manufacturer => Test(manufacturer),
            GroupRuleField.Model => Test(model),
            GroupRuleField.IpAddress => Split(ips).Any(Test),
            GroupRuleField.Tag => Split(tags).Any(Test),
            _ => Test(name),
        };
    }

    /// <summary>Whether a client is a member: all conditions match (or one, with <see cref="ClientGroup.MatchAny"/>); a group without a condition has no member.</summary>
    private static bool Matches(ClientGroup g, ClientFacts c)
    {
        var conditions = Conditions(g);
        bool One(GroupCondition x) => Matches(x.Field, x.Operator, x.Value, c.Name, c.OperatingSystem, c.Domain, c.Manufacturer, c.Model, c.IpAddresses, c.Tags);
        return conditions.Count > 0 && (g.MatchAny ? conditions.Any(One) : conditions.All(One));
    }

    private static Task<List<ClientFacts>> FactsAsync(AppDbContext db, long? clientId, CancellationToken ct)
        => db.Clients.AsNoTracking().Where(c => clientId == null || c.Id == clientId)
            .Select(c => new ClientFacts(c.Id, c.Name, c.OperatingSystem, c.Domain, c.Manufacturer, c.Model, c.IpAddresses, c.Tags)).ToListAsync(ct);

    /// <summary>Computes the members of one dynamic group anew.</summary>
    public static async Task RefreshAsync(AppDbContext db, ClientGroup group, CancellationToken ct = default)
    {
        if (group.Kind != GroupKind.Dynamic)
        {
            return;
        }

        var clients = await FactsAsync(db, null, ct);
        await ApplyAsync(db, group.Id, clients.Where(c => Matches(group, c)).Select(c => c.Id).ToHashSet(), null, ct);
    }

    /// <summary>Puts one client into the dynamic groups it matches now and out of the others (after its inventory or a change of its tags).</summary>
    public static async Task RefreshClientAsync(AppDbContext db, long clientId, CancellationToken ct = default)
    {
        var facts = (await FactsAsync(db, clientId, ct)).FirstOrDefault();
        if (facts is null)
        {
            return;
        }

        var groups = await db.ClientGroups.AsNoTracking().Where(g => g.Kind == GroupKind.Dynamic).ToListAsync(ct);
        foreach (var g in groups)
        {
            await ApplyAsync(db, g.Id, Matches(g, facts) ? [clientId] : [], clientId, ct);
        }
    }

    /// <summary>Every dynamic group anew (the maintenance does it every few minutes).</summary>
    public static async Task RefreshAllAsync(AppDbContext db, CancellationToken ct = default)
    {
        var groups = await db.ClientGroups.AsNoTracking().Where(g => g.Kind == GroupKind.Dynamic).ToListAsync(ct);
        if (groups.Count == 0)
        {
            return;
        }

        var clients = await FactsAsync(db, null, ct);
        foreach (var g in groups)
        {
            await ApplyAsync(db, g.Id, clients.Where(c => Matches(g, c)).Select(c => c.Id).ToHashSet(), null, ct);
        }
    }

    /// <summary>Makes the members of a group <paramref name="wanted"/> (only for <paramref name="onlyClient"/> when it is given).</summary>
    private static async Task ApplyAsync(AppDbContext db, long groupId, HashSet<long> wanted, long? onlyClient, CancellationToken ct)
    {
        var current = await db.ClientGroupMembers.AsNoTracking().Where(m => m.ClientGroupId == groupId && (onlyClient == null || m.ClientId == onlyClient))
            .Select(m => m.ClientId).ToListAsync(ct);
        var gone = current.Where(id => !wanted.Contains(id)).ToList();
        var added = wanted.Where(id => !current.Contains(id)).ToList();

        // set by set in the database: the planner (every minute) and an inventory may refresh the same group at once – a row that is there already or
        // gone already is no error
        if (gone.Count > 0)
        {
            await db.ClientGroupMembers.Where(m => m.ClientGroupId == groupId && gone.Contains(m.ClientId)).ExecuteDeleteAsync(ct);
        }

        if (added.Count > 0)
        {
            var now = DateTime.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ClientGroupMember" ("ClientGroupId", "ClientId", "CreateDate", "UpdateDate")
                SELECT {groupId}, id, {now}, {now} FROM unnest({added.ToArray()}) AS id
                ON CONFLICT ("ClientGroupId", "ClientId") DO NOTHING
                """, ct);
        }
    }

    public static string RuleText(GroupRuleField field, GroupRuleOperator op, string? value)
        => $"{Localization.Text.T("Groups.Field." + field)} {Localization.Text.T("Groups.Operator." + op)} „{value}“";

    /// <summary>"Computer name starts with „BER-“ and tag is „laptop“".</summary>
    public static string RuleText(IReadOnlyList<GroupCondition> conditions, bool matchAny)
        => string.Join(Localization.Text.T(matchAny ? "Groups.Or" : "Groups.And"), conditions.Select(c => RuleText(c.Field, c.Operator, c.Value)));
}
