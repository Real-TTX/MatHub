using System.Security.Cryptography;
using System.Text;
using MatHub.Data;
using MatHub.Protocol;
using MatHub.Services.Access;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services.Clients;

public sealed record EnrollmentKeyRow(long Id, string Name, string Prefix, bool IsActive, DateTime? Expires, int MaxUses, int UseCount, bool AutoApprove, string? GroupName, string? GroupColour, bool Usable);

public sealed record EnrollmentKeyForm(string Name, bool IsActive, DateOnly? Expires, int MaxUses, bool AutoApprove, long? GroupId);

/// <summary>The outcome of an enrollment: the answer for the agent, or why it was refused (HTTP status and a word for the log).</summary>
public sealed record EnrollOutcome(EnrollResponse? Response, int Status, string? Error);

/// <summary>
/// The enrollment keys and the enrollment of an agent. A key is shown once when it is made (<c>mhk_</c> + 32 characters); only its SHA-256 is kept. An agent
/// enrolls with a key (or with the token of a rollout task) and gets its own token and secret; a reinstall on the same computer (the same MachineId) takes its
/// client back instead of making a second one.
/// </summary>
public static class EnrollmentService
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    public static string NewSecret(string prefix, int length = 32)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return prefix + new string(chars);
    }

    public static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>Compares a secret with a stored hash in constant time.</summary>
    public static bool Verify(string? secret, string hash)
        => !string.IsNullOrEmpty(secret) && hash.Length == 64
           && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(secret)), Encoding.ASCII.GetBytes(hash));

    public static bool IsUsable(EnrollmentKey k, DateTime now) => k.IsActive && (k.ExpiresDate is null || k.ExpiresDate > now) && (k.MaxUses == 0 || k.UseCount < k.MaxUses);

    // ---- the keys ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The keys an actor may see and change: all for someone who reaches all clients, else those that put their clients into a group the actor reaches
    /// (another team's key could otherwise be pointed at one's own group, and their new computers would land in one's reach).
    /// </summary>
    private static IQueryable<EnrollmentKey> Keys(AppDbContext db, Actor actor)
    {
        if (actor.ReachesAll)
        {
            return db.EnrollmentKeys;
        }

        var groups = AccessRules.Groups(db, actor).Select(g => g.Id);
        return db.EnrollmentKeys.Where(k => k.ClientGroupId != null && groups.Contains(k.ClientGroupId.Value));
    }

    public static async Task<IReadOnlyList<EnrollmentKeyRow>?> ListAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ClientsEnroll))
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var keys = await Keys(db, actor).AsNoTracking().Include(k => k.ClientGroup).OrderBy(k => k.Name).ToListAsync(ct);
        return keys.Select(k => new EnrollmentKeyRow(k.Id, k.Name, k.Prefix, k.IsActive, k.ExpiresDate, k.MaxUses, k.UseCount, k.AutoApprove, k.ClientGroup?.Name, k.ClientGroup?.Colour, IsUsable(k, now))).ToList();
    }

    public static async Task<EnrollmentKey?> GetAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
        => actor.Can(Permission.ClientsEnroll) ? await Keys(db, actor).AsNoTracking().FirstOrDefaultAsync(k => k.Id == id, ct) : null;

    /// <summary>Saves a key; a new key is answered with its plain text (the only time it is seen) as the second value.</summary>
    public static async Task<Outcome<(EnrollmentKey Key, string? Plain)>> SaveAsync(AppDbContext db, Actor actor, long? id, EnrollmentKeyForm form, string timeZone, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ClientsEnroll))
        {
            return Outcome<(EnrollmentKey, string?)>.Denied();
        }

        var name = form.Name.Trim();
        if (name.Length == 0)
        {
            return Outcome<(EnrollmentKey, string?)>.Fail(Localization.Text.T("Common.NameRequired"));
        }

        if (form.GroupId is { } groupId && !await AccessRules.Groups(db, actor).AnyAsync(g => g.Id == groupId, ct))
        {
            return Outcome<(EnrollmentKey, string?)>.Denied();
        }

        // a key whose clients land outside every group would be outside the reach of a limited role: such a person must name one of their groups
        if (form.GroupId is null && !actor.ReachesAll)
        {
            return Outcome<(EnrollmentKey, string?)>.Fail(Localization.Text.T("Enrollment.GroupRequired"));
        }

        EnrollmentKey key;
        string? plain = null;
        if (id is { } existing)
        {
            var found = await Keys(db, actor).FirstOrDefaultAsync(k => k.Id == existing, ct);
            if (found is null)
            {
                return Outcome<(EnrollmentKey, string?)>.Denied();
            }

            key = found;
        }
        else
        {
            plain = NewSecret("mhk_");
            key = new EnrollmentKey { KeyHash = Hash(plain), Prefix = plain[..10] };
            db.EnrollmentKeys.Add(key);
        }

        key.Name = name.Length > 120 ? name[..120] : name;
        key.IsActive = form.IsActive;
        key.ExpiresDate = form.Expires is { } day ? Dates.EndOfDayUtc(day, timeZone) : null;
        key.MaxUses = Math.Max(0, form.MaxUses);
        key.AutoApprove = form.AutoApprove;
        key.ClientGroupId = form.GroupId;
        ActivityLog.Add(db, actor, "enrollment", id is null ? "create" : "update", key.Name);
        await db.SaveChangesAsync(ct);
        return Outcome<(EnrollmentKey, string?)>.Success((key, plain));
    }

    public static async Task<Outcome<string>> DeleteAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ClientsEnroll))
        {
            return Outcome<string>.Denied();
        }

        var key = await Keys(db, actor).FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key is null)
        {
            return Outcome<string>.Denied();
        }

        db.EnrollmentKeys.Remove(key);
        ActivityLog.Add(db, actor, "enrollment", "delete", key.Name);
        await db.SaveChangesAsync(ct);
        return Outcome<string>.Success(key.Name);
    }

    // ---- the enrollment of an agent ---------------------------------------------------------------------------------------------

    public static async Task<EnrollOutcome> EnrollAsync(AppDbContext db, EnrollRequest request, CancellationToken ct = default)
    {
        var hostName = (request.HostName ?? string.Empty).Trim();
        if (hostName.Length == 0 || hostName.Length > 120)
        {
            return new EnrollOutcome(null, StatusCodes.Status400BadRequest, "host name missing");
        }

        long? groupId = null;
        long? keyId = null;
        var pending = false;
        RolloutTask? rollout = null;
        var now = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(request.Key))
        {
            var hash = Hash(request.Key.Trim());
            var key = await db.EnrollmentKeys.FirstOrDefaultAsync(k => k.KeyHash == hash, ct);
            if (key is null || !IsUsable(key, now))
            {
                return new EnrollOutcome(null, StatusCodes.Status403Forbidden, "unknown or used-up key");
            }

            // counted in the database: a classroom enrolling at once does not pass the limit
            var counted = await db.EnrollmentKeys.Where(k => k.Id == key.Id && (k.MaxUses == 0 || k.UseCount < k.MaxUses))
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.UseCount, k => k.UseCount + 1), ct);
            if (counted == 0)
            {
                return new EnrollOutcome(null, StatusCodes.Status403Forbidden, "unknown or used-up key");
            }

            groupId = key.ClientGroupId;
            keyId = key.Id;
            pending = !key.AutoApprove;
        }
        else if (Guid.TryParse(request.RolloutToken, out var token))
        {
            rollout = await db.RolloutTasks.Include(t => t.Profile).FirstOrDefaultAsync(t => t.Token == token && (t.Status == RolloutStatus.Waiting || t.Status == RolloutStatus.Installing), ct);
            if (rollout is null)
            {
                return new EnrollOutcome(null, StatusCodes.Status403Forbidden, "unknown rollout");
            }

            groupId = rollout.Profile?.ClientGroupId;
        }
        else
        {
            return new EnrollOutcome(null, StatusCodes.Status403Forbidden, "no key");
        }

        // the same computer again (a reinstall of the agent): its client, with a new secret. A computer installed again by a rollout task gets a new
        // machine id from Windows: its client is found by the MAC address of the task (it keeps its groups and its history).
        var machineId = Trim(request.MachineId, 64);
        var client = machineId is null ? null : await db.Clients.Where(c => c.MachineId == machineId).OrderByDescending(c => c.LastSeenDate).FirstOrDefaultAsync(ct);
        if (client is null && rollout?.MacAddress is { Length: > 0 } mac)
        {
            client = await db.Clients.Where(c => c.MacAddresses != null && c.MacAddresses.Contains(mac)).OrderByDescending(c => c.LastSeenDate).FirstOrDefaultAsync(ct);
        }

        // a client whose agent called just now is not simply taken over: that may be a copy of the computer (a cloned VM) or someone who read its
        // machine id – it waits for an approval (no jobs, no secrets until someone looked at it)
        if (client is not null && rollout is null && client.Status == ClientStatus.Active && ClientService.IsOnline(client.LastSeenDate))
        {
            ActivityLog.Add(db, Actor.System, "client", "enroll-again", client.Name, hostName, client.Id);
            pending = true;
        }

        var secret = NewSecret(string.Empty, 40);
        if (client is null)
        {
            client = new Client { Name = hostName, MachineId = machineId, EnrollmentKeyId = keyId, Status = pending ? ClientStatus.Pending : ClientStatus.Active };
            db.Clients.Add(client);
        }
        else if (client.Status == ClientStatus.Pending && !pending)
        {
            // a key without approval (or a rollout) approves a computer that waited
            client.Status = ClientStatus.Active;
        }
        else if (client.Status == ClientStatus.Active && pending)
        {
            // taken over with a key that wants an approval: it waits for one again (its jobs and secrets only after it)
            client.Status = ClientStatus.Pending;
        }

        client.MachineId = machineId ?? client.MachineId;

        client.Token = Guid.NewGuid();
        client.SecretHash = Hash(secret);
        client.Name = hostName;
        client.OperatingSystem = Trim(request.OperatingSystem, 200) ?? client.OperatingSystem;
        client.AgentVersion = Trim(request.AgentVersion, 60);
        client.LastSeenDate = now;
        client.InventoryRequested = true;
        await db.SaveChangesAsync(ct);

        if (groupId is { } g && !await db.ClientGroupMembers.AnyAsync(m => m.ClientGroupId == g && m.ClientId == client.Id, ct)
            && await db.ClientGroups.AnyAsync(x => x.Id == g && x.Kind == GroupKind.Static, ct))
        {
            db.ClientGroupMembers.Add(new ClientGroupMember { ClientGroupId = g, ClientId = client.Id });
        }

        if (rollout is not null)
        {
            rollout.Status = RolloutStatus.Completed;
            rollout.CompletedDate = now;
            rollout.ClientId = client.Id;
        }

        ActivityLog.Add(db, Actor.System, "client", "enroll", client.Name, rollout is null ? null : "rollout", client.Id);
        await db.SaveChangesAsync(ct);
        await GroupService.RefreshClientAsync(db, client.Id, ct);

        return new EnrollOutcome(new EnrollResponse
        {
            ClientToken = client.Token.ToString(),
            Secret = secret,
            ClientName = client.Name,
            Pending = client.Status == ClientStatus.Pending,
        }, StatusCodes.Status200OK, null);
    }

    private static string? Trim(string? text, int max)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > max ? t[..max] : t;
    }
}
