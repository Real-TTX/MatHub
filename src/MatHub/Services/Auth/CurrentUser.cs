using MatHub.Data;
using MatHub.Services.Access;

namespace MatHub.Services;

/// <summary>What the pages need to know about the signed-in person, read once per request from the session (no query of its own).</summary>
/// <param name="Permissions">The permissions of the person's role (and those MatIdentity added); an administrator has all of them anyway.</param>
/// <param name="AllClients">The role reaches every client; else only the members of <paramref name="GroupIds"/>.</param>
public sealed record UserSnapshot(
    long Id, string Email, string DisplayName, UserRole Role, string Theme, string Scheme, string TimeZone, string Language,
    IReadOnlySet<string> Permissions, bool AllClients, IReadOnlyList<long> GroupIds, string? RoleName);

/// <summary>The signed-in user of the request. Background work and the agent API have none (<see cref="Id"/> is <c>null</c>).</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor)
{
    public const string ItemKey = "MatHub.User";

    /// <summary>Set by background work that acts for a person (the audit columns name them).</summary>
    public long? ActingUserId { get; set; }

    public UserSnapshot? Snapshot => accessor.HttpContext?.Items[ItemKey] as UserSnapshot;

    public bool IsAuthenticated => Snapshot is not null;

    public long? Id => Snapshot?.Id ?? ActingUserId;

    /// <summary>The id of the signed-in person; throws when there is none (only call it behind the sign-in).</summary>
    public long RequiredId => Snapshot?.Id ?? throw new InvalidOperationException("No signed-in user.");

    public bool IsAdmin => Snapshot?.Role == UserRole.Admin;

    public string DisplayName => Snapshot?.DisplayName ?? string.Empty;

    public string TimeZone => Snapshot?.TimeZone ?? "Europe/Berlin";

    public DateOnly Today => Dates.Today(TimeZone);

    /// <summary>The person as the business layer sees them: permissions and the clients they reach.</summary>
    public Actor Actor => Snapshot is { } s
        ? new Actor(s.Id, s.Role == UserRole.Admin, s.Permissions, s.AllClients, s.GroupIds)
        : new Actor(null, false, new HashSet<string>(), false, []);

    public bool Can(string permission) => Actor.Can(permission);

    public string RoleName => Snapshot?.Role == UserRole.Admin
        ? Localization.Text.T("Common.Role.Admin")
        : Snapshot?.RoleName ?? Localization.Text.T("Common.Role.User");
}
