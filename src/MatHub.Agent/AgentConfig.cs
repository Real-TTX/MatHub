using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace MatHub.Agent;

/// <summary>The version of the agent (the same as the server's build: <c>0.1.42-20261112</c>, <c>nightly-42-20261112</c>, <c>local-20261112</c>).</summary>
public static class AgentInfo
{
    public const string ServiceName = "MatHub Agent";

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "local";

    /// <summary>
    /// Where the agent keeps its settings, its log and its downloads: <c>%ProgramData%\MatHub Agent</c>; <c>MATHUB_AGENT_DATA</c> names another folder (a test as a
    /// normal user: then the folder keeps the rights it has).
    /// </summary>
    public static string DataDirectory => OwnDataDirectory
        ? Environment.GetEnvironmentVariable("MATHUB_AGENT_DATA")!
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MatHub Agent");

    /// <summary>The data folder is named by <c>MATHUB_AGENT_DATA</c> (a test, not the service).</summary>
    public static bool OwnDataDirectory => Environment.GetEnvironmentVariable("MATHUB_AGENT_DATA") is { Length: > 0 };

    /// <summary>Where the installed agent lives: <c>%ProgramFiles%\MatHub Agent\mathub-agent.exe</c>.</summary>
    public static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MatHub Agent");

    public static string InstalledExe => Path.Combine(InstallDirectory, "mathub-agent.exe");
}

/// <summary>
/// The identity of the agent: the server, the token of its client and its secret. Kept in <c>%ProgramData%\MatHub Agent\agent.json</c>; the secret is encrypted
/// with DPAPI (this computer only), and the folder may only be read and written by SYSTEM and the administrators.
/// </summary>
public sealed record AgentConfig(string ServerUrl, string ClientToken, string Secret)
{
    private sealed record Stored(string ServerUrl, string ClientToken, string SecretProtected);

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MatHub.Agent.Secret");

    public static string FilePath => Path.Combine(AgentInfo.DataDirectory, "agent.json");

    public static AgentConfig? Load()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath));
        if (stored is null)
        {
            return null;
        }

        var secret = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored.SecretProtected), Entropy, DataProtectionScope.LocalMachine));
        return new AgentConfig(stored.ServerUrl, stored.ClientToken, secret);
    }

    public void Save()
    {
        EnsureDataDirectory();
        var protectedSecret = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(Secret), Entropy, DataProtectionScope.LocalMachine));
        var json = JsonSerializer.Serialize(new Stored(ServerUrl, ClientToken, protectedSecret), new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }

    /// <summary>The data folder with an ACL of its own: SYSTEM and the administrators (full), nobody else; a standard user cannot read the identity or plant files.</summary>
    public static void EnsureDataDirectory()
    {
        var dir = new DirectoryInfo(AgentInfo.DataDirectory);
        if (!dir.Exists)
        {
            dir.Create();
        }

        if (AgentInfo.OwnDataDirectory)
        {
            return;
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(security);
    }

    /// <summary>A hash of the MachineGuid of Windows: the server recognises a reinstall on the same computer by it.</summary>
    public static string? MachineId()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return key?.GetValue("MachineGuid") is string guid
            ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("mathub:" + guid.Trim().ToLowerInvariant())))[..32]
            : null;
    }
}
