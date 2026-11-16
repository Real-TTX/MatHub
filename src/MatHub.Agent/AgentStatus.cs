using System.Text.Json;
using System.Text.Json.Serialization;

namespace MatHub.Agent;

/// <summary>What the agent knows about itself just now: the service says the first five, the tray adds the last two when it cannot reach the service.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentCondition>))]
public enum AgentCondition
{
    /// <summary>The service started and has not reached MatHub yet.</summary>
    Connecting,

    /// <summary>The last call to MatHub worked.</summary>
    Connected,

    /// <summary>MatHub knows the computer, an administrator has not approved it yet.</summary>
    Pending,

    /// <summary>MatHub cannot be reached; the agent tries again by itself.</summary>
    Unreachable,

    /// <summary>MatHub refuses the agent (the client was removed or switched off).</summary>
    Rejected,

    /// <summary>The service is installed but does not run (the tray found no status channel).</summary>
    ServiceStopped,

    /// <summary>The service is not installed on this computer.</summary>
    NotInstalled,
}

/// <summary>The four states of a tray (MatDesign TRAY-02): a dot on the icon and a word in the menu.</summary>
public enum TrayState
{
    /// <summary>Green (<c>--mat-success</c>).</summary>
    Running,

    /// <summary>Amber (<c>--mat-warning</c>): waiting or attention.</summary>
    Waiting,

    /// <summary>Red (<c>--mat-danger</c>): stopped or an error.</summary>
    Stopped,

    /// <summary>Grey (<c>--mat-text-muted</c>): not set up.</summary>
    NotSetUp,
}

/// <summary>
/// The snapshot the service writes to its status channel (<see cref="AgentInfo.StatusPipeName"/>): the state, the server, the last contact, the version.
/// Never the token or the secret of the client (AGENT-03): any local user may read it.
/// </summary>
public sealed record AgentStatus
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public AgentCondition Condition { get; init; } = AgentCondition.Connecting;

    /// <summary>The address of MatHub (no secret: it is in the install command).</summary>
    public string? Server { get; init; }

    public DateTime? LastContact { get; init; }

    public DateTime? Started { get; init; }

    public string? Version { get; init; }

    /// <summary>The state of the tray for a condition (TRAY-02).</summary>
    public static TrayState StateOf(AgentCondition condition) => condition switch
    {
        AgentCondition.Connected => TrayState.Running,
        AgentCondition.Connecting or AgentCondition.Pending or AgentCondition.Unreachable => TrayState.Waiting,
        AgentCondition.Rejected or AgentCondition.ServiceStopped => TrayState.Stopped,
        _ => TrayState.NotSetUp,
    };

    [JsonIgnore]
    public TrayState State => StateOf(Condition);

    /// <summary>The service runs (its status channel answered), so it can be stopped or restarted.</summary>
    [JsonIgnore]
    public bool ServiceRuns => Condition is not (AgentCondition.ServiceStopped or AgentCondition.NotInstalled);
}

/// <summary>The state of the service, kept by its loop and read by the status channel.</summary>
public sealed class AgentStatusBoard(string server)
{
    private readonly object _gate = new();
    private readonly DateTime _started = DateTime.UtcNow;
    private AgentCondition _condition = AgentCondition.Connecting;
    private DateTime? _lastContact;

    /// <summary>A call reached MatHub: connected, or waiting for an approval.</summary>
    public void Contact(bool pending)
    {
        lock (_gate)
        {
            _condition = pending ? AgentCondition.Pending : AgentCondition.Connected;
            _lastContact = DateTime.UtcNow;
        }
    }

    public void Set(AgentCondition condition)
    {
        lock (_gate)
        {
            _condition = condition;
        }
    }

    public AgentStatus Snapshot()
    {
        lock (_gate)
        {
            return new AgentStatus { Condition = _condition, Server = server, LastContact = _lastContact, Started = _started, Version = AgentInfo.Version };
        }
    }
}
