using System.Text;
using MatHub.Data;

namespace MatHub.Services.Packages;

/// <summary>What MatHub reads from an installer: the kind, the product, the version, the platform and the silent switches it proposes.</summary>
public sealed record InstallerFacts(
    PackageKind Kind, string? Framework, string? ProductName, string? Version, string? Manufacturer, string? ProductCode, InstallerArchitecture Architecture, string? Arguments);

/// <summary>
/// Reads an installer so a person does not have to know it: an MSI is a database (a compound file): its table Property names the product, the version, the
/// manufacturer and the product code; an EXE is recognised by the setup it was made with (Inno, NSIS, WiX Burn, InstallShield, Advanced Installer) and gets its
/// silent switches; the platform comes from the template of the MSI, the PE header or the file name.
/// </summary>
public static class InstallerAnalysis
{
    public static InstallerFacts Analyze(string path, string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var byName = ArchitectureOfName(fileName);
        if (extension == ".msi")
        {
            var properties = MsiProperties(path);
            var template = properties.GetValueOrDefault("Template");
            return new InstallerFacts(PackageKind.Msi, "msi", properties.GetValueOrDefault("ProductName"), properties.GetValueOrDefault("ProductVersion"),
                properties.GetValueOrDefault("Manufacturer"), properties.GetValueOrDefault("ProductCode"), byName ?? ArchitectureOfTemplate(path) ?? InstallerArchitecture.Neutral, null);
        }

        var (framework, arguments) = Framework(path);
        return new InstallerFacts(PackageKind.Exe, framework, null, VersionOfName(fileName), null, null, byName ?? Machine(path) ?? InstallerArchitecture.Neutral, arguments);
    }

    /// <summary>x64, x86, arm64 in the name of a file ("7z2408-x64.exe", "setup_arm64.msi", "win32").</summary>
    public static InstallerArchitecture? ArchitectureOfName(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("arm64") || n.Contains("aarch64"))
        {
            return InstallerArchitecture.Arm64;
        }

        if (n.Contains("x64") || n.Contains("amd64") || n.Contains("win64") || n.Contains("x86_64") || n.Contains("64bit") || n.Contains("64-bit"))
        {
            return InstallerArchitecture.X64;
        }

        return n.Contains("x86") || n.Contains("win32") || n.Contains("32bit") || n.Contains("32-bit") || n.Contains("i386") ? InstallerArchitecture.X86 : null;
    }

    /// <summary>A version in a file name ("Firefox Setup 131.0.2.exe" is 131.0.2).</summary>
    public static string? VersionOfName(string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(name), @"(\d+(?:\.\d+){1,3})");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>The setup an EXE was made with, by the marks it carries, and its silent switches.</summary>
    public static (string? Framework, string? Arguments) Framework(string path)
    {
        var text = Latin1Head(path, 8 * 1024 * 1024);
        if (text.Contains("Inno Setup", StringComparison.Ordinal))
        {
            return ("inno", "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-");
        }

        if (text.Contains("Nullsoft", StringComparison.Ordinal) || text.Contains("NullsoftInst", StringComparison.Ordinal))
        {
            return ("nullsoft", "/S");
        }

        if (text.Contains(".wixburn", StringComparison.Ordinal) || text.Contains("WixBundle", StringComparison.Ordinal))
        {
            return ("burn", "/quiet /norestart");
        }

        if (text.Contains("InstallShield", StringComparison.Ordinal))
        {
            return ("installshield", "/s /v\"/qn /norestart\"");
        }

        if (text.Contains("Advanced Installer", StringComparison.Ordinal))
        {
            return ("advancedinstaller", "/exenoui /qn /norestart");
        }

        return (null, null);
    }

    /// <summary>The platform of a PE file (its machine field); null when it is not one.</summary>
    public static InstallerArchitecture? Machine(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var reader = new BinaryReader(file);
            if (file.Length < 0x40 || reader.ReadUInt16() != 0x5A4D)
            {
                return null;
            }

            file.Position = 0x3C;
            var pe = reader.ReadInt32();
            if (pe <= 0 || pe > file.Length - 6)
            {
                return null;
            }

            file.Position = pe;
            if (reader.ReadUInt32() != 0x00004550)
            {
                return null;
            }

            return reader.ReadUInt16() switch
            {
                0x8664 => InstallerArchitecture.X64,
                0xAA64 => InstallerArchitecture.Arm64,
                0x014C => InstallerArchitecture.X86,
                _ => null,
            };
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string Latin1Head(string path, int max)
    {
        using var file = File.OpenRead(path);
        var buffer = new byte[(int)Math.Min(max, file.Length)];
        var read = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return Encoding.Latin1.GetString(buffer, 0, read);
    }

    // ---- MSI: a database in a compound file ------------------------------------------------------------------------------------------

    private const string Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz._";

    /// <summary>The names of the streams of an MSI are packed: two characters of the name in one character of the stream name, 0x4840 marks a table.</summary>
    public static string DecodeStreamName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            if (c is >= (char)0x3800 and < (char)0x4800)
            {
                var v = c - 0x3800;
                sb.Append(Chars[v & 0x3F]).Append(Chars[(v >> 6) & 0x3F]);
            }
            else if (c is >= (char)0x4800 and < (char)0x4840)
            {
                sb.Append(Chars[c - 0x4800]);
            }
            else if (c == (char)0x4840)
            {
                sb.Append('!');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>The table Property of an MSI (name → value); empty when the file is no MSI.</summary>
    public static Dictionary<string, string> MsiProperties(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var root = OpenMcdf.RootStorage.OpenRead(path);
            var names = root.EnumerateEntries().Where(e => e.Type == OpenMcdf.EntryType.Stream).ToDictionary(e => DecodeStreamName(e.Name), e => e.Name);
            if (!names.TryGetValue("!_StringPool", out var poolName) || !names.TryGetValue("!_StringData", out var dataName) || !names.TryGetValue("!Property", out var propertyName))
            {
                return result;
            }

            byte[] Read(string stream)
            {
                using var s = root.OpenStream(stream);
                using var copy = new MemoryStream();
                s.CopyTo(copy);
                return copy.ToArray();
            }

            var pool = Read(poolName);
            var data = Read(dataName);
            var table = Read(propertyName);
            if (pool.Length < 4)
            {
                return result;
            }

            // the header: the code page, and whether string references take 3 bytes
            var codepage = BitConverter.ToUInt16(pool, 0) | ((BitConverter.ToUInt16(pool, 2) & 0x7FFF) << 16);
            var wide = (BitConverter.ToUInt16(pool, 2) & 0x8000) != 0;
            var encoding = codepage == 65001 ? Encoding.UTF8 : Encoding.Latin1;
            var strings = new List<string> { string.Empty };
            var offset = 0;
            var entries = (pool.Length - 4) / 4;
            for (var i = 0; i < entries; i++)
            {
                int length = BitConverter.ToUInt16(pool, 4 + i * 4);
                var refs = BitConverter.ToUInt16(pool, 6 + i * 4);
                if (length == 0 && refs != 0 && i + 1 < entries)
                {
                    // a string over 64 KB: the next entry holds its length
                    length = (refs << 16) | BitConverter.ToUInt16(pool, 4 + (i + 1) * 4);
                    i++;
                }

                if (offset + length > data.Length)
                {
                    break;
                }

                strings.Add(encoding.GetString(data, offset, length));
                offset += length;
            }

            // the table, column by column: Property (string), Value (string)
            var size = wide ? 3 : 2;
            var rows = table.Length / (2 * size);
            int Ref(int at) => wide ? table[at] | (table[at + 1] << 8) | (table[at + 2] << 16) : table[at] | (table[at + 1] << 8);
            for (var r = 0; r < rows; r++)
            {
                var key = Ref(r * size);
                var value = Ref((rows + r) * size);
                if (key < strings.Count && value < strings.Count && strings[key].Length > 0)
                {
                    result[strings[key]] = strings[value];
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or ArgumentException or InvalidOperationException)
        {
            // no MSI (or a broken one): nothing is known
        }

        return result;
    }

    /// <summary>The platform an MSI is for: its summary information names it ("x64;1033").</summary>
    private static InstallerArchitecture? ArchitectureOfTemplate(string path)
    {
        try
        {
            using var root = OpenMcdf.RootStorage.OpenRead(path);
            if (!root.EnumerateEntries().Any(e => e.Name == "\u0005SummaryInformation"))
            {
                return null;
            }

            using var s = root.OpenStream("\u0005SummaryInformation");
            using var copy = new MemoryStream();
            s.CopyTo(copy);
            var text = Encoding.Latin1.GetString(copy.ToArray());
            var platforms = System.Text.RegularExpressions.Regex.Match(text, @"(Intel64|x64|AMD64|Arm64|Intel|Arm)\s*;");
            return platforms.Success
                ? platforms.Groups[1].Value.ToLowerInvariant() switch
                {
                    "x64" or "amd64" or "intel64" => InstallerArchitecture.X64,
                    "arm64" => InstallerArchitecture.Arm64,
                    "intel" => InstallerArchitecture.X86,
                    _ => null,
                }
                : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
