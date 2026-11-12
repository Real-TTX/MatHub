using System.Text;
using MatHub.Data;
using MatHub.Services.Access;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Secret = MatHub.Data.Secret;

namespace MatHub.Services.Scripts;

public sealed record SecretRow(long Id, string Name, string? Description, string? UserName, DateTime Updated, int Scripts);

/// <param name="Value">The value; empty on an edit keeps the one there is.</param>
public sealed record SecretForm(string Name, string? Description, string? UserName, string? Value);

/// <summary>
/// The secrets of the scripts (static business layer): passwords and keys a script needs (a domain join, a Wi-Fi, a local administrator). Their values are
/// encrypted with DataProtection and never shown again; a script names a secret (<c>Get-MatHubSecret 'NAME'</c>, <c>$env:MATHUB_SECRET_NAME</c>), and the server
/// hands the value to the agent only with a run of that script (<see cref="ResolveAsync"/>); the agent hides it in the output. Anybody who may edit scripts can
/// still write a script that uses a secret: the secret protects the value against being read in MatHub, its database and its logs, not against its use.
/// </summary>
public static class SecretService
{
    public const string ProtectorPurpose = "MatHub.Scripts.Secrets";

    public static async Task<IReadOnlyList<SecretRow>?> ListAsync(AppDbContext db, Actor actor, string? search, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.SecretsManage))
        {
            return null;
        }

        var q = db.Secrets.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = $"%{search.Trim()}%";
            q = q.Where(x => EF.Functions.ILike(x.Name, s) || EF.Functions.ILike(x.Description ?? string.Empty, s) || EF.Functions.ILike(x.UserName ?? string.Empty, s));
        }

        var rows = await q.OrderBy(x => x.Name).Select(x => new { x.Id, x.Name, x.Description, x.UserName, x.UpdateDate }).ToListAsync(ct);
        var result = new List<SecretRow>();
        foreach (var r in rows)
        {
            result.Add(new SecretRow(r.Id, r.Name, r.Description, r.UserName, r.UpdateDate, await UsesAsync(db, r.Name, ct)));
        }

        return result;
    }

    /// <summary>The names of the secrets, for the editors of the scripts (the values stay where they are).</summary>
    public static async Task<IReadOnlyList<string>> NamesAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
        => actor.Can(Permission.ScriptsView) || actor.Can(Permission.SecretsManage)
            ? await db.Secrets.AsNoTracking().OrderBy(s => s.Name).Select(s => s.Name).ToListAsync(ct)
            : [];

    public static async Task<Secret?> GetAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
        => actor.Can(Permission.SecretsManage) ? await db.Secrets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) : null;

    /// <summary>A name as a secret has it: capitals, digits and _ (spaces and - become _).</summary>
    public static string Normalize(string? name)
    {
        var sb = new StringBuilder();
        foreach (var c in (name ?? string.Empty).Trim().ToUpperInvariant())
        {
            sb.Append(c is ' ' or '-' or '.' ? '_' : c);
        }

        return sb.ToString();
    }

    public static async Task<Outcome<Secret>> SaveAsync(AppDbContext db, Actor actor, IDataProtectionProvider protection, long? id, SecretForm form, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.SecretsManage))
        {
            return Outcome<Secret>.Denied();
        }

        var name = Normalize(form.Name);
        if (name.Length == 0)
        {
            return Outcome<Secret>.Fail(Localization.Text.T("Common.NameRequired"));
        }

        if (!ScriptBlocks.IsSecretName(name))
        {
            return Outcome<Secret>.Fail(Localization.Text.T("Secrets.Error.Name"));
        }

        if (await db.Secrets.AnyAsync(s => s.Name == name && s.Id != (id ?? 0), ct))
        {
            return Outcome<Secret>.Fail(Localization.Text.T("Secrets.Error.Taken", name));
        }

        Secret secret;
        if (id is { } existing)
        {
            var found = await db.Secrets.FirstOrDefaultAsync(s => s.Id == existing, ct);
            if (found is null)
            {
                return Outcome<Secret>.Denied();
            }

            secret = found;
        }
        else
        {
            if (string.IsNullOrEmpty(form.Value))
            {
                return Outcome<Secret>.Fail(Localization.Text.T("Secrets.Error.Value"));
            }

            secret = new Secret();
            db.Secrets.Add(secret);
        }

        secret.Name = name;
        secret.Description = Cut(form.Description, 500);
        secret.UserName = Cut(form.UserName, 200);
        if (!string.IsNullOrEmpty(form.Value))
        {
            if (form.Value.Length > 1000)
            {
                return Outcome<Secret>.Fail(Localization.Text.T("Secrets.Error.Long"));
            }

            secret.ValueProtected = protection.CreateProtector(ProtectorPurpose).Protect(form.Value);
        }

        ActivityLog.Add(db, actor, "secret", id is null ? "create" : "update", secret.Name);
        await db.SaveChangesAsync(ct);
        return Outcome<Secret>.Success(secret);
    }

    public static async Task<Outcome<string>> DeleteAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.SecretsManage))
        {
            return Outcome<string>.Denied();
        }

        var secret = await db.Secrets.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (secret is null)
        {
            return Outcome<string>.Denied();
        }

        // the name is free again for a new secret
        secret.Name = $"{secret.Name}~{secret.Id}";
        secret.ValueProtected = string.Empty;
        db.Secrets.Remove(secret);
        ActivityLog.Add(db, actor, "secret", "delete", secret.Name[..secret.Name.LastIndexOf('~')]);
        await db.SaveChangesAsync(ct);
        return Outcome<string>.Success(secret.Name[..secret.Name.LastIndexOf('~')]);
    }

    /// <summary>How many scripts name the secret.</summary>
    public static Task<int> UsesAsync(AppDbContext db, string name, CancellationToken ct = default)
    {
        var escaped = name.Replace("_", "\\_", StringComparison.Ordinal);
        var variable = $"%MATHUB\\_SECRET\\_{escaped}%";
        var secret = $"%-MatHubSecret '{escaped}'%";
        var credential = $"%-MatHubCredential '{escaped}'%";
        return db.Scripts.CountAsync(s => EF.Functions.Like(s.Content, variable, "\\") || EF.Functions.Like(s.Content, secret, "\\") || EF.Functions.Like(s.Content, credential, "\\"), ct);
    }

    /// <summary>
    /// The values of the secrets a script names (those that exist): NAME and, with a user name, NAME_USER. Only the agent of a run gets them; a value that cannot
    /// be decrypted (the keys of the server changed) is left out, and the script says which secret did not arrive.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>?> ResolveAsync(AppDbContext db, IDataProtectionProvider protection, string? script, CancellationToken ct = default)
    {
        var names = ScriptBlocks.SecretsIn(script);
        if (names.Count == 0)
        {
            return null;
        }

        var list = names.ToList();
        var secrets = await db.Secrets.AsNoTracking().Where(s => list.Contains(s.Name)).ToListAsync(ct);
        if (secrets.Count == 0)
        {
            return null;
        }

        var protector = protection.CreateProtector(ProtectorPurpose);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in secrets)
        {
            try
            {
                values[s.Name] = protector.Unprotect(s.ValueProtected);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(s.UserName))
            {
                values[s.Name + "_USER"] = s.UserName;
            }
        }

        return values.Count == 0 ? null : values;
    }

    private static string? Cut(string? text, int max)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > max ? t[..max] : t;
    }
}
