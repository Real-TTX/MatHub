namespace MatHub.Services.Rollout;

/// <summary>
/// The files of the network boot. The image brings the boot loaders (<c>netboot/</c> next to the app: <c>ipxe.efi</c>, <c>snponly.efi</c>, <c>undionly.kpxe</c> with
/// MatHub's script built in, and <c>wimboot</c>); a file of the same name in <c>/data/netboot/tftp</c> wins over it. The files of the boot images lie in
/// <c>/data/netboot/images/{id}/</c>.
/// </summary>
public sealed class NetbootFiles(IConfiguration configuration)
{
    /// <summary>The files a boot image of each kind has.</summary>
    public static IReadOnlyList<string> FilesOf(Data.BootImageKind kind) => kind == Data.BootImageKind.WindowsPe ? ["boot.wim", "BCD", "boot.sdi"] : ["kernel", "initrd"];

    /// <summary>The boot loaders a DHCP server names (option 67), with what they are for.</summary>
    public static IReadOnlyList<(string File, string For)> Loaders { get; } =
    [
        ("ipxe.efi", "UEFI x64"),
        ("snponly.efi", "UEFI x64 (SNP)"),
        ("undionly.kpxe", "BIOS"),
    ];

    public string DataRoot => Path.Combine(configuration["DataDirectory"] ?? "/data", "netboot");

    public string BundledRoot => configuration["NetbootDirectory"] ?? Path.Combine(AppContext.BaseDirectory, "netboot");

    public string TftpRoot => Path.Combine(DataRoot, "tftp");

    /// <summary>A boot loader or wimboot: the one in the data volume, else the one of the image; null when neither is there.</summary>
    public string? Loader(string name)
    {
        var file = Path.GetFileName(name);
        if (file.Length == 0 || file != name)
        {
            return null;
        }

        var own = Path.Combine(TftpRoot, file);
        if (File.Exists(own))
        {
            return own;
        }

        var bundled = Path.Combine(BundledRoot, file);
        return File.Exists(bundled) ? bundled : null;
    }

    public string ImageDirectory(long imageId) => Path.Combine(DataRoot, "images", imageId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The path of a file of a boot image, when it is one of its kind and it is there.</summary>
    public string? ImageFile(long imageId, Data.BootImageKind kind, string name)
    {
        var known = FilesOf(kind).FirstOrDefault(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
        if (known is null)
        {
            return null;
        }

        var path = Path.Combine(ImageDirectory(imageId), known);
        return File.Exists(path) ? path : null;
    }

    public async Task SaveImageFileAsync(long imageId, string name, Stream content, CancellationToken ct)
    {
        var dir = ImageDirectory(imageId);
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, "." + Guid.NewGuid().ToString("N") + ".part");
        await using (var target = File.Create(temp))
        {
            await content.CopyToAsync(target, ct);
        }

        File.Move(temp, Path.Combine(dir, name), overwrite: true);
    }

    public void DeleteImage(long imageId)
    {
        var dir = ImageDirectory(imageId);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
