using System.Globalization;
using MatHub.Agent;

// The MatHub agent. One program for everything (MatDesign SETUP-05: exit code 0 when it worked, else a message on the error output and in setup.log):
//   mathub-agent install [--quiet] --server https://hub.example.com --key mhk_...   enroll, install as a service, start   (as administrator; --url/--code say the same)
//   mathub-agent install --server http://hub:4910 --rollout <token>       the same for a computer of a rollout (the answer file runs it)
//   mathub-agent enroll --server … --key …                               enroll only, without the service (a test; MATHUB_AGENT_DATA names the folder)
//   mathub-agent uninstall [--quiet] [--delete-data]                     remove the service, the program, the tray, the kiosk and the entry in Installed apps;
//                                                                         the data stays unless --delete-data. Without --quiet: the window (Installed apps opens it)
//   mathub-agent run                                                      the service itself (also from a console, to watch it)
//   mathub-agent inventory                                                write the inventory of this computer as JSON
//   mathub-agent tray                                                     the symbol beside the clock: state, Info, the kiosk, the dialogs of scripts (every sign-in)
//   mathub-agent kiosk                                                    the kiosk window (start menu "MatHub Kiosk")
//   mathub-agent ask --data <base64> [--no-wait]                          a dialog of a script (Invoke-MatHubDialog): prints the answer as JSON
//   mathub-agent --version                                                the version
//   mathub-agent apply-update --target … --staged … --to … --service …   the updater (started by the agent itself)
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// "--version" only as the only argument: the updater has options of its own
if (args is ["--version"])
{
    Console.WriteLine(AgentInfo.Version);
    return 0;
}

switch (args.FirstOrDefault())
{
    case "install":
        // --quiet: the install never asks anything anyway (a missing value fails at once)
        return await Setup.InstallAsync(Option("--server") ?? Option("--url"), Option("--key") ?? Option("--code"), Option("--rollout"));
    case "enroll":
        return await Setup.EnrollOnlyAsync(Option("--server"), Option("--key"));
    case "uninstall":
        // quiet: no window (also where nobody could see one: a script of a service); --keep-config is what it does anyway
        if (args.Contains("--quiet") || (!Environment.UserInteractive && Option("--snapshot") is null))
        {
            var removed = Setup.Uninstall(deleteData: args.Contains("--delete-data"));
            if (removed == 0)
            {
                Setup.RemoveProgramFolderLater();
            }

            return removed;
        }

        return MatHub.Agent.Ui.UiApp.Uninstall(args);
    case "inventory":
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(await Inventory.CollectAsync(CancellationToken.None), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true }));
        return 0;
    case "ask":
        return await UiPipe.AskAsync(Option("--data"), args.Contains("--no-wait"));
    case "tray":
    case "kiosk":
        return MatHub.Agent.Ui.UiApp.Run(args[0], args);
    case "apply-update":
        return Updater.Apply(Option("--target")!, Option("--staged")!, Option("--to") ?? "?", Option("--service") ?? AgentInfo.ServiceName);
    case "run":
    case null:
        break;
    default:
        Console.Error.WriteLine("Usage: mathub-agent install [--quiet] --server <address> --key <key> | uninstall [--quiet] [--delete-data] | run | inventory | tray | kiosk | --version");
        return 64;
}

// the service: refuses to start without an identity instead of asking the server in vain
AgentConfig? config;
try
{
    config = AgentConfig.Load();
}
catch (Exception ex)
{
    Console.Error.WriteLine("The settings of the agent cannot be read: " + ex.Message);
    return 2;
}

if (config is null)
{
    Console.Error.WriteLine($"The agent is not enrolled ({AgentConfig.FilePath} is missing). Run: mathub-agent install --server <address> --key <key>");
    return 2;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = AgentInfo.ServiceName);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(new AgentClient(config));
builder.Services.AddSingleton<JobRunner>();
builder.Services.AddSingleton<Updater>();
builder.Services.AddSingleton<AgentSignal>();
builder.Services.AddSingleton(new AgentStatusBoard(config.ServerUrl));
builder.Services.AddHostedService<StatusPipe>();
builder.Services.AddSingleton<UiBroker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UiBroker>());
builder.Services.AddHostedService<AgentWorker>();
await builder.Build().RunAsync();
return 0;
