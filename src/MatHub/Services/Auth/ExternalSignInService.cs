using System.Security.Cryptography;
using System.Text;
using MatHub.Data;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services;

/// <summary>A person as MatIdentity names it: the subject (<c>sub</c>), the address, the names, the role claim of this app (or null) and its permissions.</summary>
/// <param name="Permissions">The permissions MatIdentity granted here (claim <c>entitlements</c>); null: MatIdentity does not decide the roles of MatHub.</param>
public sealed record ExternalIdentity(string Subject, string? Email, string? Name, string? PreferredUserName, string? Role, IReadOnlyList<string>? Permissions = null);

/// <summary>
/// The outcome of <see cref="ExternalSignInService.SignInExternalAsync"/>: the account to sign in, or the id of the account that has the address and has to prove
/// itself with its password before it is linked (<see cref="LinkUserId"/>), or the reason for a refusal.
/// </summary>
public sealed record ExternalSignInResult(User? User, long? LinkUserId, string? Error)
{
    public static ExternalSignInResult SignedIn(User user) => new(user, null, null);

    public static ExternalSignInResult LinkRequired(long userId) => new(null, userId, null);

    public static ExternalSignInResult Refused(string error) => new(null, null, error);
}

/// <summary>
/// How a person signed in by MatIdentity becomes an account of MatHub (MatDesign docs/PLATFORM.md, "Sign-in with MatIdentity"): found by
/// <see cref="User.ExternalSubject"/>; else, when an account has the same address, that account is <b>not</b> entered: it is linked only after its own sign-in
/// with its password (<see cref="LinkExternalAsync"/>), because the address of an account proves nothing (anyone may register with any address); else a new
/// account is made (no password). The claim <c>role</c> (<c>admin</c>, or the key of a built-in role: <c>technician</c>,
/// <c>operator</c>, <c>viewer</c>) sets the role at every sign-in, except that the last active administrator is never demoted; without the claim MatHub keeps its own roles (the first account of all is the administrator).
/// </summary>
public sealed class ExternalSignInService(AppDbContext db, AccountService accounts, ILogger<ExternalSignInService> logger)
{
    /// <summary>The domain of the address of an account whose person has no address MatHub may use (none, or one another account has).</summary>
    public const string PlaceholderDomain = "matidentity.invalid";

    public async Task<ExternalSignInResult> SignInExternalAsync(ExternalIdentity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.Subject))
        {
            return ExternalSignInResult.Refused("MatIdentity did not send a subject (sub).");
        }

        var subject = identity.Subject.Trim();
        var user = await db.Users.FirstOrDefaultAsync(u => u.ExternalSubject == subject, ct);
        if (user is not null)
        {
            if (!user.IsActive)
            {
                return ExternalSignInResult.Refused("The account is disabled in MatHub.");
            }

            await ApplyRoleAsync(user, identity.Role, created: false, ct);
            ApplyPermissions(user, identity);
            user.LastLoginDate = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return ExternalSignInResult.SignedIn(user);
        }

        var email = AccountService.Normalize(identity.Email);
        if (AccountService.IsEmail(email))
        {
            // The address is unique in MatHub (also of a deleted account): at most one match.
            var match = await db.Users.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(u => u.Email == email, ct);
            if (match is not null)
            {
                if (match.ExternalSubject is not null)
                {
                    logger.LogWarning("MatIdentity subject {Subject} matches user {UserId}, which is linked with another subject; refused", subject, match.Id);
                    return ExternalSignInResult.Refused("The account with this address is linked with another person of MatIdentity.");
                }

                if (!match.IsActive || match.UpdateState == SoftEntity.Deleted)
                {
                    return ExternalSignInResult.Refused("The account is disabled in MatHub.");
                }

                if (!string.IsNullOrEmpty(match.PasswordHash))
                {
                    // Not linked by the address alone: the person proves the account with its password first.
                    logger.LogInformation("MatIdentity subject {Subject} matches user {UserId} by address; the link waits for its password", subject, match.Id);
                    return ExternalSignInResult.LinkRequired(match.Id);
                }

                // An account without a password cannot prove itself: a new account is made beside it (without the address).
                email = null;
            }
        }
        else
        {
            email = null;
        }

        return ExternalSignInResult.SignedIn(await CreateAsync(subject, email, identity, ct));
    }

    /// <summary>
    /// Links the account <paramref name="userId"/> with the person of <paramref name="identity"/> after the account proved itself with its password (the pending
    /// link of <see cref="SignInExternalAsync"/>): sets the subject and follows the role claim like a sign-in through MatIdentity. Refused when the account is
    /// disabled or linked meanwhile, or when another account has the subject by now.
    /// </summary>
    public async Task<ExternalSignInResult> LinkExternalAsync(long userId, ExternalIdentity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.Subject))
        {
            return ExternalSignInResult.Refused("MatIdentity did not send a subject (sub).");
        }

        var subject = identity.Subject.Trim();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive)
        {
            return ExternalSignInResult.Refused("The account is disabled in MatHub.");
        }

        if (user.ExternalSubject is not null || await db.Users.IgnoreQueryFilters().AnyAsync(u => u.ExternalSubject == subject, ct))
        {
            logger.LogWarning("User {UserId} was not linked with MatIdentity subject {Subject}: one of them is linked already", userId, subject);
            return ExternalSignInResult.Refused("The account is linked with MatIdentity already.");
        }

        user.ExternalSubject = subject;
        await ApplyRoleAsync(user, identity.Role, created: false, ct);
        ApplyPermissions(user, identity);
        await db.SaveChangesAsync(ct);
        SessionService.Invalidate();
        logger.LogInformation("User {UserId} linked with MatIdentity subject {Subject} after its password", user.Id, subject);
        return ExternalSignInResult.SignedIn(user);
    }

    /// <summary>The address of an account made for a person without a usable address: unique, and no mail could ever go there.</summary>
    public static string PlaceholderEmail(string subject)
        => "id-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subject)))[..24].ToLowerInvariant() + "@" + PlaceholderDomain;

    private async Task<User> CreateAsync(string subject, string? email, ExternalIdentity identity, CancellationToken ct)
    {
        var first = !await accounts.AnyUserAsync();
        var address = email ?? PlaceholderEmail(subject);
        var name = FirstNonEmpty(identity.Name, identity.PreferredUserName, email?.Split('@')[0]) ?? "MatIdentity";
        var user = new User
        {
            Email = address,
            DisplayName = name.Length > 120 ? name[..120] : name,

            // No password anybody knows: this person signs in through MatIdentity.
            PasswordHash = string.Empty,
            Role = first ? UserRole.Admin : UserRole.User,
            ExternalSubject = subject,
            LastLoginDate = DateTime.UtcNow,
        };
        await ApplyRoleAsync(user, identity.Role, created: true, ct);
        ApplyPermissions(user, identity);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        await accounts.ProvisionAsync(user);
        logger.LogInformation("User {UserId} created at the first sign-in through MatIdentity (subject {Subject})", user.Id, subject);
        return user;
    }

    /// <summary>
    /// The claim <c>role</c>: <c>admin</c> makes an administrator; the key of a built-in role (<c>technician</c>, <c>operator</c>, <c>viewer</c>) makes a user with
    /// that role; <c>user</c> a user without one. Never demotes the last active administrator; anything else, or none, leaves it.
    /// </summary>
    private async Task ApplyRoleAsync(User user, string? claim, bool created, CancellationToken ct)
    {
        var key = claim?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        long? accessRoleId = null;
        UserRole role;
        if (key == "admin")
        {
            role = UserRole.Admin;
        }
        else if (key == "user")
        {
            role = UserRole.User;
        }
        else if (await db.AccessRoles.Where(r => r.Key == key).Select(r => (long?)r.Id).FirstOrDefaultAsync(ct) is { } found)
        {
            role = UserRole.User;
            accessRoleId = found;
        }
        else
        {
            return;
        }

        if (!created && role != UserRole.Admin && user.Role == UserRole.Admin && user.IsActive
            && !await db.Users.AnyAsync(u => u.Id != user.Id && u.Role == UserRole.Admin && u.IsActive, ct))
        {
            logger.LogWarning("MatIdentity asks for role {Role} for user {UserId}, the last active administrator; the role stays", key, user.Id);
            return;
        }

        user.Role = role;
        if (role == UserRole.User)
        {
            user.AccessRoleId = accessRoleId;
        }

        SessionService.Invalidate();
    }

    /// <summary>The permissions MatIdentity granted (claim <c>entitlements</c>): with a role (MatIdentity decides here) they are kept on the account, else none.</summary>
    private static void ApplyPermissions(User user, ExternalIdentity identity)
    {
        user.IdentityPermissions = identity.Role is not null && identity.Permissions is { } permissions
            ? MatHub.Services.Access.Permission.Join(MatHub.Services.Access.Permission.Parse(string.Join(',', permissions)))
            : string.Empty;
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
