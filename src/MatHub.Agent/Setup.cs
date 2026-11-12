using System.Diagnostics;
using System.Security.Principal;
using MatHub.Protocol;

namespace MatHub.Agent;

/// <summary>
/// The commands of an administrator: <c>install</c> enrolls the computer, copies the agent to <c>%ProgramFiles%\MatHub Agent</c> and registers and starts the
/// service (it restarts by itself after a crash); <c>uninstall</c> removes the service, the program and its data. A reinstall keeps nothing but the client
/// on the server, which finds the computer again by its MachineId.
/// </summary>
public static class Setup
{
    public static async Task<int> InstallAsync(string? server, string? key, string? rollout)
    {
        if (!IsAdministrator())
        {
            Console.Error.WriteLine("Please run this in a command prompt or PowerShell as administrator.");
            return 5;
        }

        if (string.IsNullOrWhiteSpace(server) || !Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            Console.Error.WriteLine("Usage: mathub-agent install --server https://hub.example.com --key mhk_...");
            return 64;
        }

        if (string.IsNullOrWhiteSpace(key) && string.IsNullOrWhiteSpace(rollout))
        {
            Console.Error.WriteLine("An enrollment key is needed: --key mhk_...");
            return 64;
        }

        Console.WriteLine($"MatHub agent {AgentInfo.Version}: enrolling {Environment.MachineName} at {server} ...");
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
            Console.Error.WriteLine("The enrollment failed: " + ex.Message);
            return 2;
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
            Console.Error.WriteLine("The service could not be created.");
            return 3;
        }

        Sc("description", $"\"{AgentInfo.ServiceName}\" \"Client management by MatHub: inventory, software, scripts.\"");
        Sc("failure", $"\"{AgentInfo.ServiceName}\" reset= 86400 actions= restart/10000/restart/60000/restart/60000");
        if (Sc("start", $"\"{AgentInfo.ServiceName}\"") != 0)
        {
            Console.Error.WriteLine("The service could not be started; see the event log (Application, MatHub Agent).");
            return 4;
        }

        Kiosk(install: true);
        Console.WriteLine(enrolled.Pending
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

    public static int Uninstall()
    {
        if (!IsAdministrator())
        {
            Console.Error.WriteLine("Please run this in a command prompt or PowerShell as administrator.");
            return 5;
        }

        Sc("stop", $"\"{AgentInfo.ServiceName}\"");
        Thread.Sleep(2000);
        Sc("delete", $"\"{AgentInfo.ServiceName}\"");
        Kiosk(install: false);
        foreach (var dir in new[] { AgentInfo.DataDirectory })
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // the running program cannot delete itself: cmd does it a moment later
        if (Directory.Exists(AgentInfo.InstallDirectory))
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{AgentInfo.InstallDirectory}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }

        Console.WriteLine("The MatHub agent is removed. Remove the client in MatHub too, if it should go.");
        return 0;
    }

    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    private static string KioskShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "MatHub Kiosk.lnk");

    /// <summary>
    /// The kiosk for the people at the computer: the tray starts at every sign-in (HKLM Run) and the start menu has "MatHub Kiosk". The uninstall removes both
    /// and ends the trays that run.
    /// </summary>
    private static void Kiosk(bool install)
    {
        try
        {
            using var run = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(RunKey);
            if (install)
            {
                run.SetValue(AgentInfo.ServiceName, $"\"{AgentInfo.InstalledExe}\" tray");
                var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is missing.");
                dynamic shell = Activator.CreateInstance(type)!;
                dynamic link = shell.CreateShortcut(KioskShortcut);
                link.TargetPath = AgentInfo.InstalledExe;
                link.Arguments = "kiosk";
                link.IconLocation = AgentInfo.InstalledExe + ",0";
                link.Description = "MatHub Kiosk";
                link.WorkingDirectory = AgentInfo.InstallDirectory;
                link.Save();
                Console.WriteLine("The kiosk is in the start menu; its symbol beside the clock comes with the next sign-in.");
            }
            else
            {
                run.DeleteValue(AgentInfo.ServiceName, throwOnMissingValue: false);
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

    private static bool IsAdministrator() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

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
