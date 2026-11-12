using System.Security.Cryptography;

namespace MatHub.Services.Packages;

/// <summary>
/// The installers of the packages in the data volume: <c>/data/packages/{package}/{installer}/{file name}</c> (one file per installer). Files of packages from
/// before the versions lie one folder up (<c>/data/packages/{package}/{file name}</c>) and are still found there.
/// </summary>
public sealed class PackageFiles(IConfiguration configuration)
{
    public string Root => Path.Combine(configuration["DataDirectory"] ?? "/data", "packages");

    private string Folder(long packageId, long installerId)
        => Path.Combine(Root, packageId.ToString(System.Globalization.CultureInfo.InvariantCulture), installerId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The path of the file of an installer, when it is there.</summary>
    public string? Find(long packageId, long installerId, string fileName)
    {
        var path = Path.Combine(Folder(packageId, installerId), SafeName(fileName));
        if (File.Exists(path))
        {
            return path;
        }

        var legacy = Path.Combine(Root, packageId.ToString(System.Globalization.CultureInfo.InvariantCulture), SafeName(fileName));
        return File.Exists(legacy) ? legacy : null;
    }

    /// <summary>Stores the file of an installer (a temporary name first, then moved): its name, size and SHA-256.</summary>
    public async Task<(string Name, long Size, string Sha256)> SaveAsync(long packageId, long installerId, string fileName, Stream content, CancellationToken ct)
    {
        var dir = Folder(packageId, installerId);
        Directory.CreateDirectory(dir);
        var name = SafeName(fileName);
        var temp = Path.Combine(dir, "." + Guid.NewGuid().ToString("N") + ".part");
        long size;
        string sha;
        await using (var target = File.Create(temp))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
            }

            size = target.Length;
            sha = Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        foreach (var old in Directory.GetFiles(dir).Where(f => f != temp))
        {
            File.Delete(old);
        }

        File.Move(temp, Path.Combine(dir, name), overwrite: true);
        return (name, size, sha);
    }

    // ---- an upload the assistant reads first (/data/packages/.staging/{token}/{file}), and takes or forgets ----------------------------------

    private string Staging => Path.Combine(Root, ".staging");

    public async Task<string> StageAsync(string fileName, Stream content, CancellationToken ct)
    {
        // what was left by an assistant nobody finished goes after a day
        if (Directory.Exists(Staging))
        {
            foreach (var old in Directory.GetDirectories(Staging).Where(d => Directory.GetCreationTimeUtc(d) < DateTime.UtcNow.AddDays(-1)))
            {
                Directory.Delete(old, recursive: true);
            }
        }

        var token = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(Staging, token);
        Directory.CreateDirectory(dir);
        await using var target = File.Create(Path.Combine(dir, SafeName(fileName)));
        await content.CopyToAsync(target, ct);
        return token;
    }

    /// <summary>The staged file of a token (a token is 32 hex digits, nothing else).</summary>
    public string? Staged(string? token)
    {
        if (token is null || !System.Text.RegularExpressions.Regex.IsMatch(token, "^[0-9a-f]{32}$"))
        {
            return null;
        }

        var dir = Path.Combine(Staging, token);
        return Directory.Exists(dir) ? Directory.GetFiles(dir).FirstOrDefault() : null;
    }

    public void Forget(string? token)
    {
        if (Staged(token) is { } file)
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    public void DeleteInstaller(long packageId, long installerId)
    {
        var dir = Folder(packageId, installerId);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    public void DeleteAll(long packageId)
    {
        var dir = Path.Combine(Root, packageId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A file name without a path and without characters Windows refuses.</summary>
    public static string SafeName(string name)
    {
        var file = Path.GetFileName(name.Replace('\\', '/'));
        var clean = new string(file.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c is not (':' or '*' or '?' or '"' or '<' or '>' or '|')).ToArray()).Trim();
        return clean.Length == 0 ? "installer.bin" : clean.Length > 200 ? clean[^200..] : clean;
    }
}
