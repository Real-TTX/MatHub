using System.Diagnostics;
using System.Security.Principal;
using MatHub.Protocol;

namespace MatHub.Agent;

/// <summary>
/// The commands of an administrator (MatDesign SETUP-03, SETUP-04, SETUP-05): <c>install</c> enrolls the computer first (a bad key installs nothing), copies the
/// agent to <c>%ProgramFiles%\MatHub Agent</c>, registers and starts the service (it restarts by itself after a crash), the tray at every sign-in, the kiosk in the
/// start menu and the entry in Installed apps; <c>uninstall</c> removes all of it and keeps the data folder unless it is asked to delete it. A reinstall finds the
/// client on the server again by its MachineId. Every message goes to the error output (or the output) and to <c>setup.log</c> in the data folder.
/// Exit codes: 0 done, 2 the enrollment failed, 3 the service could not be created, 4 it could not be started, 5 no administrator, 64 wrong arguments.
/// </summary>
public static class Setup
{
    public static async Task<int> InstallAsync(string? server, string? key, string? rollout)
    {
        if (!IsAdministrator())
        {
            return Fail(5, "Please run this in a command prompt or PowerShell as administrator.");
        }

        if (string.IsNullOrWhiteSpace(server) || !Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return Fail(64, "Usage: mathub-agent install [--quiet] --server https://hub.example.com --key mhk_...   (or --url and --code)");
        }

        if (string.IsNullOrWhiteSpace(key) && string.IsNullOrWhiteSpace(rollout))
        {
            return Fail(64, "An enrollment key is needed: --key mhk_...");
        }

        Say($"{AgentInfo.DisplayName} {AgentInfo.Version}: enrolling {Environment.MachineName} at {server} ...");
        EnrollResponse enrolled;
        try
        {
            enrolled = await AgentClient.EnrollAsync(server, new EnrollRequest
            {
                Key = key,
                RolloutToken = rollout,
                HostName = Environment.MachineName,
                MachineId = AgentConfig.MachineId(),
                OperatingSystem = Environment.OSVersion.VersionString,
                AgentVersion = AgentInfo.Version,
            }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            return Fail(2, "The enrollment failed: " + ex.Message);
        }

        // an agent that runs already stops before its file is replaced
        Sc("stop", $"\"{AgentInfo.ServiceName}\"");
        Thread.Sleep(2000);

        Directory.CreateDirectory(AgentInfo.InstallDirectory);
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("The path of the agent is not known.");
        if (!string.Equals(self, AgentInfo.InstalledExe, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(self, AgentInfo.InstalledExe, overwrite: true);
        }

        new AgentConfig(server.TrimEnd('/'), enrolled.ClientToken, enrolled.Secret).Save();

        // the service: automatic start, restarted after a crash (10 s, then 60 s)
        Sc("delete", $"\"{AgentInfo.ServiceName}\"");
        Thread.Sleep(1000);
        var created = Sc("create", $"\"{AgentInfo.ServiceName}\" binPath= \"\\\"{AgentInfo.InstalledExe}\\\" run\" start= auto DisplayName= \"{AgentInfo.ServiceName}\"");
        if (created != 0)
        {
            return Fail(3, "The service could not be created.");
        }

        Sc("description", $"\"{AgentInfo.ServiceName}\" \"Client management by MatHub: inventory, software, scripts.\"");
        Sc("failure", $"\"{AgentInfo.ServiceName}\" reset= 86400 actions= restart/10000/restart/60000/restart/60000");
        if (Sc("start", $"\"{AgentInfo.ServiceName}\"") != 0)
        {
            return Fail(4, "The service could not be started; see the event log (Application, MatHub Agent).");
        }

        Kiosk(install: true);
        Register();
        Say(enrolled.Pending
            ? $"Installed. {enrolled.ClientName} waits for an approval in MatHub."
            : $"Installed. {enrolled.ClientName} is a client of MatHub now.");
        return 0;
    }

    /// <summary>Enrolls this computer and keeps the identity, without the service (a test: <c>mathub-agent run</c> afterwards, in the same folder).</summary>
    public static async Task<int> EnrollOnlyAsync(string? server, string? key)
    {
        if (!AgentInfo.OwnDataDirectory && !IsAdministrator())
        {
            Console.Error.WriteLine("As a normal user, name a folder for the agent first: set MATHUB_AGENT_DATA=C:\\Temp\\mathub-agent");
            return 5;
        }

        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(key))
        {
            Console.Error.WriteLine("Usage: mathub-agent enroll --server https://hub.example.com --key mhk_...");
            return 64;
        }

        var enrolled = await AgentClient.EnrollAsync(server, new EnrollRequest
        {
            Key = key,
            HostName = Environment.MachineName,
            MachineId = AgentConfig.MachineId(),
            OperatingSystem = Environment.OSVersion.VersionString,
            AgentVersion = AgentInfo.Version,
        }, CancellationToken.None);
        new AgentConfig(server.TrimEnd('/'), enrolled.ClientToken, enrolled.Secret).Save();
        Console.WriteLine($"Enrolled as {enrolled.ClientName}{(enrolled.Pending ? " (waits for an approval)" : string.Empty)}. Start it with: mathub-agent run");
        return 0;
    }

    /// <summary>
    /// Removes the service, the tray, the kiosk and the entry in Installed apps; the data folder (the identity, the settings) stays unless <paramref name="deleteData"/>
    /// (SETUP-04: "Also delete my data" is off). The program folder goes when this process has ended: call <see cref="RemoveProgramFolderLater"/> last.
    /// </summary>
    public static int Uninstall(bool deleteData)
    {
        if (!IsAdministrator())
        {
            return Fail(5, "Please run this in a command prompt or PowerShell as administrator.");
        }

        Say($"Removing {AgentInfo.DisplayName}{(deleteData ? " and its data" : string.Empty)} ...");
        Sc("stop", $"\"{AgentInfo.ServiceName}\"");
        Thread.Sleep(2000);
        Sc("delete", $"\"{AgentInfo.ServiceName}\"");
        Kiosk(install: false);
        Unregister();

        // what a run left is no data: gone in any case; the rest only when asked
        _dataDeleted = deleteData;
        foreach (var dir in deleteData ? [AgentInfo.DataDirectory] : new[] { "run", "packages" }.Select(d => Path.Combine(AgentInfo.DataDirectory, d)))
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }

            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"{dir} could not be deleted: {ex.Message}");
            }
        }

        Say(deleteData
            ? $"{AgentInfo.DisplayName} and its data are removed. Remove the client in MatHub too, if it should go."
            : $"{AgentInfo.DisplayName} is removed; its settings stay in {AgentInfo.DataDirectory}. Remove the client in MatHub too, if it should go.");
        return 0;
    }

    /// <summary>The running program cannot delete itself: cmd removes the program folder a moment after this process has ended.</summary>
    public static void RemoveProgramFolderLater()
    {
        if (Directory.Exists(AgentInfo.InstallDirectory))
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{AgentInfo.InstallDirectory}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
    }

    /// <summary>
    /// At each start of the installed service (as SYSTEM): the entry in Installed apps with this version (a self-update does not run the install) and the names of
    /// AGENT-01 for an agent installed before them (the autostart value of the tray).
    /// </summary>
    public static void Refresh(ILogger logger)
    {
        if (AgentInfo.OwnDataDirectory || !string.Equals(Environment.ProcessPath, AgentInfo.InstalledExe, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Register();
            using var run = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue(AgentInfo.OldTrayRunValue) is string old)
            {
                run.SetValue(AgentInfo.TrayRunValue, old);
                run.DeleteValue(AgentInfo.OldTrayRunValue, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            logger.LogWarning("The entry in Installed apps could not be written: {Message}", ex.Message);
        }
    }

    /// <summary>The entry in Installed apps (SETUP-04), for the machine.</summary>
    private static void Register()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(InstalledApp.KeyPath);
        var size = File.Exists(AgentInfo.InstalledExe) ? new FileInfo(AgentInfo.InstalledExe).Length : 0;
        foreach (var (name, value) in InstalledApp.Values(AgentInfo.InstalledExe, AgentInfo.InstallDirectory, AgentInfo.Version, AgentInfo.Publisher, size))
        {
            key.SetValue(name, value, value is int ? Microsoft.Win32.RegistryValueKind.DWord : Microsoft.Win32.RegistryValueKind.String);
        }
    }

    private static void Unregister()
    {
        try
        {
            Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(InstalledApp.KeyPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Console.Error.WriteLine("The entry in Installed apps could not be removed: " + ex.Message);
        }
    }

    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    private static string KioskShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "MatHub Kiosk.lnk");

    /// <summary>
    /// The kiosk for the people at the computer: the tray starts at every sign-in (HKLM Run, the value <c>MatHub Agent Tray</c>) and the start menu has
    /// "MatHub Kiosk". The uninstall removes both and ends the trays that run.
    /// </summary>
    private static void Kiosk(bool install)
    {
        try
        {
            using var run = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(RunKey);
            run.DeleteValue(AgentInfo.OldTrayRunValue, throwOnMissingValue: false);
            if (install)
            {
                run.SetValue(AgentInfo.TrayRunValue, $"\"{AgentInfo.InstalledExe}\" tray");
                var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is missing.");
                dynamic shell = Activator.CreateInstance(type)!;
                dynamic link = shell.CreateShortcut(KioskShortcut);
                link.TargetPath = AgentInfo.InstalledExe;
                link.Arguments = "kiosk";
                link.IconLocation = AgentInfo.InstalledExe + ",0";
                link.Description = "MatHub Kiosk";
                link.WorkingDirectory = AgentInfo.InstallDirectory;
                link.Save();
                Say("The kiosk is in the start menu; its symbol beside the clock comes with the next sign-in.");
            }
            else
            {
                run.DeleteValue(AgentInfo.TrayRunValue, throwOnMissingValue: false);
                File.Delete(KioskShortcut);
                foreach (var process in Process.GetProcessesByName("mathub-agent").Where(p => p.Id != Environment.ProcessId))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // gone already, or of another session that ends with it
                    }
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Console.Error.WriteLine("The kiosk could not be " + (install ? "set up" : "removed") + ": " + ex.Message);
        }
    }

    public static bool IsAdministrator() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>A message for the administrator: the output and the log of the setup.</summary>
    private static void Say(string text)
    {
        Console.WriteLine(text);
        Log(text);
    }

    /// <summary>A failure: the error output, the log, the exit code (SETUP-05).</summary>
    private static int Fail(int code, string text)
    {
        Console.Error.WriteLine(text);
        Log($"failed ({code}): {text}");
        return code;
    }

    /// <summary>The uninstall deleted the data folder: no log brings it back.</summary>
    private static bool _dataDeleted;

    /// <summary><c>setup.log</c> in the data folder (only SYSTEM and administrators may read it); without the rights to write there, the output is all there is.</summary>
    private static void Log(string text)
    {
        if (_dataDeleted || (!AgentInfo.OwnDataDirectory && !IsAdministrator()))
        {
            return;
        }

        try
        {
            AgentConfig.EnsureDataDirectory();
            File.AppendAllText(Path.Combine(AgentInfo.DataDirectory, "setup.log"), $"{DateTime.UtcNow:O} {text}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // not an administrator: nothing to write into
        }
    }

    private static int Sc(string verb, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), verb + " " + arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }
}
