using System.Text.Json;
using System.Text.Json.Serialization;
using MatHub.Data;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services.Auth;

/// <summary>
/// The data of a person under the GDPR: the export of everything that is about them (Art. 15 and 20, a JSON file), and the erasure of the account (Art. 17).
/// <para>
/// The export holds the account and its settings, the role, the jobs the person started, the activity of the person and the sessions. No secret: no password
/// hash, no token.
/// </para>
/// <para>
/// The erasure deletes the account, its sessions and its activity for good. What the person made (scripts, packages, jobs) belongs to the installation and
/// stays, without their name.
/// </para>
/// </summary>
public sealed class AccountDataService(AppDbContext db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The export as JSON (UTF-8).</summary>
    public async Task<byte[]> ExportAsync(long userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().Include(u => u.AccessRole).FirstAsync(u => u.Id == userId, ct);
        var export = new
        {
            Format = "MatHub export 1",
            ExportedAt = DateTime.UtcNow,
            Account = new
            {
                user.Email,
                user.DisplayName,
                user.Role,
                AccessRole = user.AccessRole?.Name,
                user.TimeZone,
                user.Language,
                user.Theme,
                user.Scheme,
                user.CreateDate,
                user.LastLoginDate,
                LinkedWithMatIdentity = user.ExternalSubject != null,
            },
            Jobs = await db.Jobs.AsNoTracking().Where(j => j.CreateUserId == userId).OrderByDescending(j => j.CreateDate).Take(5000)
                .Select(j => new { j.Name, j.Kind, j.CreateDate, Clients = j.Runs.Count }).ToListAsync(ct),
            Activity = await db.Activities.AsNoTracking().Where(a => a.UserId == userId).OrderByDescending(a => a.OccurredDate).Take(5000)
                .Select(a => new { a.OccurredDate, a.Area, a.Action, a.Target, a.Detail }).ToListAsync(ct),
            Sessions = await db.Sessions.AsNoTracking().Where(s => s.UserId == userId)
                .Select(s => new { s.CreateDate, s.LastSeenDate, s.ExpiresDate, s.IsRevoked, s.UserAgent, s.IpAddress }).ToListAsync(ct),
        };

        return JsonSerializer.SerializeToUtf8Bytes(export, Json);
    }

    /// <summary>Erases the account for good (see the class). The caller checks the password and that it is not the last administrator.</summary>
    public async Task EraseAsync(long userId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Activities.Where(a => a.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Jobs.Where(j => j.CreateUserId == userId).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreateUserId, (long?)null), ct);
        await db.Users.IgnoreQueryFilters().Where(u => u.Id == userId).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Is the person the last active administrator (who must not go, or nobody can run the installation)?</summary>
    public async Task<bool> IsLastAdminAsync(long userId)
        => await db.Users.AnyAsync(u => u.Id == userId && u.Role == UserRole.Admin && u.IsActive)
           && !await db.Users.AnyAsync(u => u.Id != userId && u.Role == UserRole.Admin && u.IsActive);
}
