using System.Security.Cryptography;
using MatHub.Data;
using MatHub.Protocol;
using MatHub.Services.Access;
using MatHub.Services.Packages;
using Microsoft.EntityFrameworkCore;

namespace MatHub.Services.Scripts;

/// <summary>The files of the scripts in the data volume: <c>/data/scripts/{script}/{name}</c>.</summary>
public sealed class ScriptFileStore(IConfiguration configuration)
{
    public string Root => Path.Combine(configuration["DataDirectory"] ?? "/data", "scripts");

    public string PathOf(long scriptId, string name) => Path.Combine(Root, scriptId.ToString(System.Globalization.CultureInfo.InvariantCulture), PackageFiles.SafeName(name));

    /// <summary>Stores a file (a temporary name first, then moved; a file of the same name is replaced): its size and SHA-256.</summary>
    public async Task<(long Size, string Sha256)> SaveAsync(long scriptId, string name, Stream content, CancellationToken ct)
    {
        var target = PathOf(scriptId, name);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".part";
        long size;
        string sha;
        await using (var file = File.Create(temp))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }

            size = file.Length;
            sha = Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        File.Move(temp, target, overwrite: true);
        return (size, sha);
    }

    public void Delete(long scriptId, string name)
    {
        var path = PathOf(scriptId, name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void Copy(long fromScript, long toScript, string name)
    {
        var from = PathOf(fromScript, name);
        if (File.Exists(from))
        {
            var to = PathOf(toScript, name);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, overwrite: true);
        }
    }
}

/// <summary>
/// The files of a script (static business layer): uploaded to MatHub, they go with every run of the script into the folder it runs in on the computer
/// (<c>./name</c> in the fields of the actions, <c>$env:MATHUB_FILES</c> in code). The agent downloads each file with the run and checks its SHA-256.
/// </summary>
public static class ScriptFileService
{
    public const long MaxSize = 512L * 1024 * 1024;

    public const int MaxFiles = 50;

    public static async Task<IReadOnlyList<ScriptFile>> ListAsync(AppDbContext db, Actor actor, long scriptId, CancellationToken ct = default)
        => actor.Can(Permission.ScriptsView)
            ? await db.ScriptFiles.AsNoTracking().Where(f => f.ScriptId == scriptId).OrderBy(f => f.Name).ToListAsync(ct)
            : [];

    public static async Task<Outcome<ScriptFile>> UploadAsync(AppDbContext db, Actor actor, ScriptFileStore store, long scriptId, string fileName, Stream content, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ScriptsEdit))
        {
            return Outcome<ScriptFile>.Denied();
        }

        var script = await db.Scripts.FirstOrDefaultAsync(s => s.Id == scriptId, ct);
        if (script is null)
        {
            return Outcome<ScriptFile>.Denied();
        }

        var name = PackageFiles.SafeName(fileName);
        var file = await db.ScriptFiles.FirstOrDefaultAsync(f => f.ScriptId == scriptId && f.Name == name, ct);
        if (file is null && await db.ScriptFiles.CountAsync(f => f.ScriptId == scriptId, ct) >= MaxFiles)
        {
            return Outcome<ScriptFile>.Fail(Localization.Text.T("ScriptFiles.Error.TooMany", MaxFiles));
        }

        var (size, sha) = await store.SaveAsync(scriptId, name, content, ct);
        if (size > MaxSize)
        {
            store.Delete(scriptId, name);
            return Outcome<ScriptFile>.Fail(Localization.Text.T("ScriptFiles.Error.TooLarge"));
        }

        if (file is null)
        {
            file = new ScriptFile { ScriptId = scriptId, Name = name };
            db.ScriptFiles.Add(file);
        }

        file.Size = size;
        file.Sha256 = sha;
        script.UpdateDate = DateTime.UtcNow;
        ActivityLog.Add(db, actor, "script", "upload", script.Name, name);
        await db.SaveChangesAsync(ct);
        return Outcome<ScriptFile>.Success(file);
    }

    public static async Task<Outcome<string>> DeleteAsync(AppDbContext db, Actor actor, ScriptFileStore store, long scriptId, long fileId, CancellationToken ct = default)
    {
        if (!actor.Can(Permission.ScriptsEdit))
        {
            return Outcome<string>.Denied();
        }

        var file = await db.ScriptFiles.Include(f => f.Script).FirstOrDefaultAsync(f => f.Id == fileId && f.ScriptId == scriptId, ct);
        if (file is null)
        {
            return Outcome<string>.Denied();
        }

        db.ScriptFiles.Remove(file);
        store.Delete(scriptId, file.Name);
        ActivityLog.Add(db, actor, "script", "delete", file.Script!.Name, file.Name);
        await db.SaveChangesAsync(ct);
        return Outcome<string>.Success(file.Name);
    }

    /// <summary>The files a run of the script downloads (a copy of the list at the time the job is made).</summary>
    public static async Task<IReadOnlyList<JobFile>?> ForJobAsync(AppDbContext db, long scriptId, CancellationToken ct = default)
    {
        var files = await db.ScriptFiles.AsNoTracking().Where(f => f.ScriptId == scriptId).OrderBy(f => f.Name).ToListAsync(ct);
        return files.Count == 0 ? null : files.Select(f => new JobFile { Name = f.Name, Url = $"/api/agent/v1/files/{f.Id}", Sha256 = f.Sha256, Size = f.Size }).ToList();
    }

    /// <summary>Whether the client may download the file now: a run of its script is running there (also as the current step of a sequence).</summary>
    public static async Task<bool> AllowedAsync(AppDbContext db, long clientId, ScriptFile file, CancellationToken ct = default)
    {
        var runs = await db.JobRuns.AsNoTracking().Include(r => r.Job)
            .Where(r => r.ClientId == clientId && r.Status == RunStatus.Running && (r.Job!.ScriptId == file.ScriptId || r.Job.Kind == JobKind.Sequence))
            .ToListAsync(ct);
        return runs.Any(r => r.Job!.Kind != JobKind.Sequence
            ? r.Job.ScriptId == file.ScriptId
            : Jobs.SequenceService.StepsOf(r.Job) is var steps && r.Step < steps.Count && steps[r.Step].ScriptId == file.ScriptId);
    }
}
