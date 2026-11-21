using MonitorSwitcher.Core.Monitors;

namespace MonitorSwitcher.Core.Storage;

/// <summary>
/// Machine state the app learns as it runs (state.json), shared by the tray app and the CLI.
/// Not user preferences: deleting it is always safe.
/// </summary>
public sealed class AppState
{
    public Dictionary<string, MonitorState> Monitors { get; set; } = [];

    public MonitorState GetOrAdd(string monitorId)
    {
        if (!Monitors.TryGetValue(monitorId, out var state))
        {
            state = new MonitorState();
            Monitors[monitorId] = state;
        }
        return state;
    }
}

public sealed class MonitorState
{
    /// <summary>Last input this app sent. Monitors such as the 32UN880 cannot report their current input.</summary>
    public string? LastInputId { get; set; }

    public DateTimeOffset? LastSwitchedAt { get; set; }

    /// <summary>Where the monitor was last reached, so it can still be found if the OS stops listing it.</summary>
    public EndpointRecord? LastEndpoint { get; set; }

    public MonitorIdentity? Identity { get; set; }

    /// <summary>Cached DDC/CI capabilities string (reading it takes a second or two).</summary>
    public string? Capabilities { get; set; }
}

/// <summary>Transport-specific address, e.g. transport "nvapi", address "0:0x100" (GPU index, output id).</summary>
public sealed class EndpointRecord
{
    public string Transport { get; set; } = "";

    public string Address { get; set; } = "";
}
