using System.Security.Cryptography;

namespace MatHub.Services.Packages;

/// <summary>The installers of the packages in the data volume: <c>/data/packages/{id}/{file name}</c> (one file per package; a new upload replaces it).</summary>
public sealed class PackageFiles(IConfiguration configuration)
{
    public string Root => Path.Combine(configuration["DataDirectory"] ?? "/data", "packages");

    /// <summary>The path of the installer of a package, when it is there.</summary>
    public string? Find(long packageId, string fileName)
    {
        var path = Path.Combine(Root, packageId.ToString(System.Globalization.CultureInfo.InvariantCulture), SafeName(fileName));
        return File.Exists(path) ? path : null;
    }

    /// <summary>Stores an installer (written to a temporary name first, then moved): its name, size and SHA-256.</summary>
    public async Task<(string Name, long Size, string Sha256)> SaveAsync(long packageId, string fileName, Stream content, CancellationToken ct)
    {
        var dir = Path.Combine(Root, packageId.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
