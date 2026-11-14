using MatHub.Data;
using MatHub.Services.Access;
using MatHub.Services.Agent;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services.Rollout;

public sealed record ImageRow(long Id, string Name, BootImageKind Kind, string? Description, IReadOnlyList<(string File, long? Size)> Files);

public sealed record ImageForm(string Name, string? Description, BootImageKind Kind, string? KernelArguments);

public sealed record ProfileRow(long Id, string Name, ProfileKind Kind, string? ImageName, string? GroupName, string? GroupColour, int Tasks);

public sealed record ProfileForm(
    string Name, string? Description, ProfileKind Kind, long? BootImageId, string? SourcePath, string? SourceUser, string? SourcePassword, int ImageIndex,
    string? ProductKey, string Locale, string TimeZone, string? AdminPassword, string? AnswerFile, string? IpxeScript, bool InstallAgent, long? ClientGroupId);

public sealed record TaskRow(long Id, string MacAddress, string HostName, string ProfileName, RolloutStatus Status, DateTime Created, DateTime? Booted, DateTime? Completed, long? ClientId);

public sealed record TaskForm(string MacAddress, string HostName, long ProfileId);

public sealed record DeviceRow(long Id, string MacAddress, string? Platform, string? Manufacturer, string? Product, string? SerialNumber, string? IpAddress, DateTime LastBoot, int Boots, string? ClientName, long? ClientId, RolloutStatus? TaskStatus);

/// <summary>
/// The rollout of computers over the network (static business layer): boot images, profiles (how a computer is installed), tasks (install this MAC address with
/// that profile at its next network boot) and the devices that booted. The boot itself is in <see cref="BootService"/>. Passwords of profiles are encrypted
/// with DataProtection and never shown again (an empty field keeps them).
/// </summary>
public static class RolloutService
{
    public const string ProtectorPurpose = "MatHub.Rollout.Secrets";

    // ---- boot images ---------------------------------------------------------------------------------------------------------

    public static async Task<IReadOnlyList<ImageRow>?> ImagesAsync(AppDbContext db, Actor actor, NetbootFiles files, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.RolloutView))
        {
            return null;
        }

        var images = await db.BootImages.AsNoTracking().OrderBy(i => i.Name).ToListAsync(ct);
        return images.Select(i => new ImageRow(i.Id, i.Name, i.Kind, i.Description, FileSizes(files, i))).ToList();
    }

    public static IReadOnlyList<(string File, long? Size)> FileSizes(NetbootFiles files, BootImage image)
        => NetbootFiles.FilesOf(image.Kind).Select(f => (f, files.ImageFile(image.Id, image.Kind, f) is { } p ? (long?)new FileInfo(p).Length : null)).ToList();

    public static async Task<BootImage?> ImageAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
        => actor.Can(Permission.RolloutView) ? await db.BootImages.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct) : null;

    public static async Task<Outcome<BootImage>> SaveImageAsync(AppDbContext db, Actor actor, long? id, ImageForm form, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.RolloutManage))
        {
            return Outcome<BootImage>.Denied();
        }

        var name = form.Name.Trim();
        if (name.Length == 0)
        {
            return Outcome<BootImage>.Fail(Localization.Text.T("Common.NameRequired"));
        }

        BootImage image;
        if (id is { } existing)
        {
            var found = await db.BootImages.FirstOrDefaultAsync(i => i.Id == existing, ct);
            if (found is null)
            {
                return Outcome<BootImage>.Denied();
            }

            image = found;
        }
        else
        {
            image = new BootImage { Kind = form.Kind };
            db.BootImages.Add(image);
        }

        image.Name = Cut(name, 120)!;
        image.Description = Cut(form.Description, 1000);
        image.KernelArguments = image.Kind == BootImageKind.Linux ? Cut(form.KernelArguments, 2000) : null;
        ActivityLog.Add(db, actor, "rollout", id is null ? "create" : "update", image.Name, "image");
        await db.SaveChangesAsync(ct);
        return Outcome<BootImage>.Success(image);
    }

    public static async Task<Outcome<BootImage>> UploadImageFileAsync(AppDbContext db, Actor actor, NetbootFiles files, long id, string slot, Stream content, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.RolloutManage))
        {
            return Outcome<BootImage>.Denied();
        }

        var image = await db.BootImages.FirstOrDefaultAsync(i => i.Id == id, ct);
        var name = image is null ? null : NetbootFiles.FilesOf(image.Kind).FirstOrDefault(f => f == slot);
        if (image is null || name is null)
        {
            return Outcome<BootImage>.Denied();
        }

        await files.SaveImageFileAsync(id, name, content, ct);
        ActivityLog.Add(db, actor, "rollout", "upload", image.Name, name);
        await db.SaveChangesAsync(ct);
        return Outcome<BootImage>.Success(image);
    }

    public static async Task<Outcome<string>> DeleteImageAsync(AppDbContext db, Actor actor, NetbootFiles files, long id, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.RolloutManage))
        {
            return Outcome<string>.Denied();
        }

        var image = await db.BootImages.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (image is null)
        {
            return Outcome<string>.Denied();
        }

        db.BootImages.Remove(image);
        ActivityLog.Add(db, actor, "rollout", "delete", image.Name, "image");
        await db.SaveChangesAsync(ct);
        files.DeleteImage(id);
        return Outcome<string>.Success(image.Name);
    }

    public static async Task<IReadOnlyList<(long Id, string Name, BootImageKind Kind)>> ImageChoicesAsync(AppDbContext db, CancellationToken ct = default)
    {
        var rows = await db.BootImages.AsNoTracking().OrderBy(i => i.Name).Select(i => new { i.Id, i.Name, i.Kind }).ToListAsync(ct);
        return rows.Select(r => (r.Id, r.Name, r.Kind)).ToList();
    }

    // ---- profiles ------------------------------------------------------------------------------------------------------------

    public static async Task<IReadOnlyList<ProfileRow>?> ProfilesAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.RolloutView))
        {
            return null;
        }

        return await db.RolloutProfiles.AsNoTracking().OrderBy(p => p.Name)
            .Select(p => new ProfileRow(p.Id, p.Name, p.Kind, p.BootImage!.Name, p.ClientGroup!.Name, p.ClientGroup.Colour,
                db.RolloutTasks.Count(t => t.ProfileId == p.Id && (t.Status == RolloutStatus.Waiting || t.Status == RolloutStatus.Installing))))
            .ToListAsync(ct);
    }

    public static async Task<RolloutProfile?> ProfileAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
        => actor.Can(Permission.RolloutView) ? await db.RolloutProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) : null;

    public static async Task<IReadOnlyList<(long Id, string Name)>> ProfileChoicesAsync(AppDbContext db, CancellationToken ct = default)
    {
        var rows = await db.RolloutProfiles.AsNoTracking().OrderBy(p => p.Name).Select(p => new { p.Id, p.Name }).ToListAsync(ct);
        return rows.Select(r => (r.Id, r.Name)).ToList();
    }

    public static async Task<Outcome<RolloutProfile>> SaveProfileAsync(AppDbContext db, Actor actor, IDataProtectionProvider protection, long? id, ProfileForm form, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.RolloutManage))
        {
            return Outcome<RolloutProfile>.Denied();
        }

        var name = form.Name.Trim();
        if (name.Length == 0)
        {
            return Outcome<RolloutProfile>.Fail(Localization.Text.T("Common.NameRequired"));
        }

        if (form.Kind != ProfileKind.Custom && form.BootImageId is null)
        {
            return Outcome<RolloutProfile>.Fail(Localization.Text.T("Profiles.Image.Required"));
        }

        if (form.Kind == ProfileKind.Windows && string.IsNullOrWhiteSpace(form.SourcePath))
        {
            return Outcome<RolloutProfile>.Fail(Localization.Text.T("Profiles.Source.Required"));
        }

        if (form.Kind == ProfileKind.Custom && string.IsNullOrWhiteSpace(form.IpxeScript))
        {
            return Outcome<RolloutProfile>.Fail(Localization.Text.T("Profiles.Ipxe.Required"));
        }

        // the passwords go into a cmd file and an XML file: a quote or a percent sign there would break them
        if ((form.SourcePassword ?? string.Empty).IndexOfAny(['"', '%']) >= 0 || (form.SourceUser ?? string.Empty).IndexOfAny(['"', '%']) >= 0)
        {
            return Outcome<RolloutProfile>.Fail(Localization.Text.T("Profiles.Password.Characters"));
        }

        if (form.BootImageId is { } imageId && !await db.BootImages.AnyAsync(i => i.Id == imageId, ct))
        {
            return Outcome<RolloutProfile>.Denied();
        }

        if (form.ClientGroupId is { } groupId && !await AccessRules.Groups(db, actor).AnyAsync(g => g.Id == groupId, ct))
        {
            return Outcome<RolloutProfile>.Denied();
        }

        RolloutProfile profile;
        if (id is { } existing)
        {
            var found = await db.RolloutProfiles.FirstOrDefaultAsync(p => p.Id == existing, ct);
            if (found is null)
            {
                return Outcome<RolloutProfile>.Denied();
            }

            profile = found;
        }
        else
        {
            profile = new RolloutProfile();
            db.RolloutProfiles.Add(profile);
        }

        var protector = protection.CreateProtector(ProtectorPurpose);
        profile.Name = Cut(name, 120)!;
        profile.Description = Cut(form.Description, 1000);
        profile.Kind = form.Kind;
        profile.BootImageId = form.Kind == ProfileKind.Custom ? null : form.BootImageId;
        profile.SourcePath = form.Kind == ProfileKind.Windows ? Cut(form.SourcePath, 500) : null;
        profile.SourceUser = form.Kind == ProfileKind.Windows ? Cut(form.SourceUser, 200) : null;
        if (!string.IsNullOrEmpty(form.SourcePassword))
        {
            profile.SourcePasswordProtected = protector.Protect(form.SourcePassword);
        }

        if (!string.IsNullOrEmpty(form.AdminPassword))
        {
            profile.AdminPasswordProtected = protector.Protect(form.AdminPassword);
        }

        profile.ImageIndex = Math.Clamp(form.ImageIndex, 1, 99);
        profile.ProductKey = Cut(form.ProductKey, 40);
        profile.Locale = Cut(form.Locale, 20) ?? "de-DE";
        profile.TimeZone = Cut(form.TimeZone, 100) ?? "W. Europe Standard Time";
        profile.AnswerFile = string.IsNullOrWhiteSpace(form.AnswerFile) ? null : form.AnswerFile.Replace("\r\n", "\n", StringComparison.Ordinal);
        profile.IpxeScript = form.Kind == ProfileKind.Custom ? form.IpxeScript?.Replace("\r\n", "\n", StringComparison.Ordinal) : null;
        profile.InstallAgent = form.InstallAgent;
        profile.ClientGroupId = form.ClientGroupId;
        ActivityLog.Add(db, actor, "rollout", id is null ? "create" : "update", profile.Name, "profile");
        await db.SaveChangesAsync(ct);
        return Outcome<RolloutProfile>.Success(profile);
    }

    public static async Task<Outcome<string>> DeleteProfileAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.RolloutManage))
        {
            return Outcome<string>.Denied();
        }

        var profile = await db.RolloutProfiles.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (profile is null)
        {
            return Outcome<string>.Denied();
        }

        db.RolloutProfiles.Remove(profile);
        await db.RolloutTasks.Where(t => t.ProfileId == id && (t.Status == RolloutStatus.Waiting || t.Status == RolloutStatus.Installing))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, RolloutStatus.Cancelled), ct);
        ActivityLog.Add(db, actor, "rollout", "delete", profile.Name, "profile");
        await db.SaveChangesAsync(ct);
        return Outcome<string>.Success(profile.Name);
    }

    // ---- tasks -----------------------------------------------------------------------------------------------------------------

    public static async Task<IReadOnlyList<TaskRow>?> TasksAsync(AppDbContext db, Actor actor, string? search, string? state, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.RolloutView))
        {
            return null;
        }

        var q = db.RolloutTasks.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = $"%{search.Trim()}%";
            q = q.Where(t => EF.Functions.ILike(t.HostName, s) || EF.Functions.ILike(t.MacAddress, s));
        }

        q = state switch
        {
            "open" => q.Where(t => t.Status == RolloutStatus.Waiting || t.Status == RolloutStatus.Installing),
            "done" => q.Where(t => t.Status == RolloutStatus.Completed),
            "cancelled" => q.Where(t => t.Status == RolloutStatus.Cancelled),
            _ => q,
        };
        return await q.OrderByDescending(t => t.CreateDate)
            .Select(t => new TaskRow(t.Id, t.MacAddress, t.HostName, t.Profile!.Name, t.Status, t.CreateDate, t.BootedDate, t.CompletedDate, t.ClientId))
            .Take(500).ToListAsync(ct);
    }

    public static async Task<RolloutTask?> TaskAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
        => actor.Can(Permission.RolloutView) ? await db.RolloutTasks.AsNoTracking().Include(t => t.Profile).FirstOrDefaultAsync(t => t.Id == id, ct) : null;

    public static async Task<Outcome<RolloutTask>> SaveTaskAsync(AppDbContext db, Actor actor, long? id, TaskForm form, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.RolloutManage))
        {
            return Outcome<RolloutTask>.Denied();
        }

        if (InventoryService.NormalizeMac(form.MacAddress) is not { } mac)
        {
            return Outcome<RolloutTask>.Fail(Localization.Text.T("Rollout.Mac.Invalid"));
        }

        var host = form.HostName.Trim().ToUpperInvariant();
        if (host.Length is 0 or > 15 || !host.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') || host.All(char.IsAsciiDigit))
        {
            return Outcome<RolloutTask>.Fail(Localization.Text.T("Rollout.HostName.Invalid"));
        }

        if (!await db.RolloutProfiles.AnyAsync(p => p.Id == form.ProfileId, ct))
        {
            return Outcome<RolloutTask>.Denied();
        }

        // a task wipes the disk of the computer: a limited role only installs computers it reaches (or new ones MatHub does not know)
        if (!actor.ReachesAll)
        {
            var reachable = AccessRules.Clients(db, actor).Select(c => c.Id);
            if (await db.Clients.AnyAsync(c => c.MacAddresses != null && c.MacAddresses.Contains(mac) && !reachable.Contains(c.Id), ct))
            {
                return Outcome<RolloutTask>.Denied();
            }
        }

        RolloutTask task;
        if (id is { } existing)
        {
            var found = await db.RolloutTasks.FirstOrDefaultAsync(t => t.Id == existing, ct);
            if (found is null)
            {
                return Outcome<RolloutTask>.Denied();
            }

            task = found;
        }
        else
        {
            // one open task per computer: an older one is cancelled
            await db.RolloutTasks.Where(t => t.MacAddress == mac && (t.Status == RolloutStatus.Waiting || t.Status == RolloutStatus.Installing))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, RolloutStatus.Cancelled), ct);
            task = new RolloutTask();
            db.RolloutTasks.Add(task);
        }

        task.MacAddress = mac;
        task.HostName = host;
        task.ProfileId = form.ProfileId;
        ActivityLog.Add(db, actor, "rollout", id is null ? "create" : "update", host, mac);
        await db.SaveChangesAsync(ct);
        return Outcome<RolloutTask>.Success(task);
    }

    /// <summary>Waiting again (to install a computer once more), or cancelled.</summary>
    public static async Task<bool> SetTaskStatusAsync(AppDbContext db, Actor actor, long id, RolloutStatus status, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.RolloutManage) || status is not (RolloutStatus.Waiting or RolloutStatus.Cancelled))
        {
            return false;
        }

        var task = await db.RolloutTasks.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (task is null)
        {
            return false;
        }

        if (status == RolloutStatus.Waiting)
        {
            await db.RolloutTasks.Where(t => t.Id != id && t.MacAddress == task.MacAddress && (t.Status == RolloutStatus.Waiting || t.Status == RolloutStatus.Installing))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, RolloutStatus.Cancelled), ct);
            task.Token = Guid.NewGuid();
            task.BootedDate = null;
            task.CompletedDate = null;
        }

        task.Status = status;
        ActivityLog.Add(db, actor, "rollout", status == RolloutStatus.Waiting ? "restart" : "cancel", task.HostName, task.MacAddress);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public static async Task<Outcome<string>> DeleteTaskAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.RolloutManage))
        {
            return Outcome<string>.Denied();
        }

        var task = await db.RolloutTasks.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (task is null)
        {
            return Outcome<string>.Denied();
        }

        db.RolloutTasks.Remove(task);
        ActivityLog.Add(db, actor, "rollout", "delete", task.HostName, task.MacAddress);
        await db.SaveChangesAsync(ct);
        return Outcome<string>.Success(task.HostName);
    }

    // ---- devices -----------------------------------------------------------------------------------------------------------------

    public static async Task<IReadOnlyList<DeviceRow>?> DevicesAsync(AppDbContext db, Actor actor, string? search, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.RolloutView))
        {
            return null;
        }

        var q = db.BootDevices.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = $"%{search.Trim()}%";
            q = q.Where(d => EF.Functions.ILike(d.MacAddress, s) || EF.Functions.ILike(d.Manufacturer ?? string.Empty, s) || EF.Functions.ILike(d.Product ?? string.Empty, s)
                             || EF.Functions.ILike(d.SerialNumber ?? string.Empty, s) || EF.Functions.ILike(d.IpAddress ?? string.Empty, s));
        }

        var rows = await q.OrderByDescending(d => d.LastBootDate).Take(500).ToListAsync(ct);
        var macs = rows.Select(r => r.MacAddress).ToList();
        var tasks = await db.RolloutTasks.AsNoTracking().Where(t => macs.Contains(t.MacAddress)).OrderByDescending(t => t.CreateDate)
            .Select(t => new { t.MacAddress, t.Status, t.ClientId }).ToListAsync(ct);
        var clients = await AccessRules.Clients(db, actor).AsNoTracking().Where(c => c.MacAddresses != null)
            .Select(c => new { c.Id, c.Name, c.MacAddresses }).ToListAsync(ct);
        return rows.Select(d =>
        {
            var task = tasks.FirstOrDefault(t => t.MacAddress == d.MacAddress);
            var client = clients.FirstOrDefault(c => c.MacAddresses!.Contains(d.MacAddress, StringComparison.Ordinal));
            return new DeviceRow(d.Id, d.MacAddress, d.Platform, d.Manufacturer, d.Product, d.SerialNumber, d.IpAddress, d.LastBootDate, d.BootCount, client?.Name, client?.Id, task?.Status);
        }).ToList();
    }

    public static async Task<Outcome<string>> DeleteDeviceAsync(AppDbContext db, Actor actor, long id, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.RolloutManage))
        {
            return Outcome<string>.Denied();
        }

        var device = await db.BootDevices.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null)
        {
            return Outcome<string>.Denied();
        }

        db.BootDevices.Remove(device);
        await db.SaveChangesAsync(ct);
        return Outcome<string>.Success(device.MacAddress);
    }

    private static string? Cut(string? text, int max)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > max ? t[..max] : t;
    }
}
