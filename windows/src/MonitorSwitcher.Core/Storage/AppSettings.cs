namespace MonitorSwitcher.Core.Storage;

/// <summary>User preferences (settings.json). Edited through the Settings window.</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    /// <summary>Monitor used when the CLI gets no --monitor and several monitors are connected.</summary>
    public string? DefaultMonitorId { get; set; }

    public List<MonitorSettings> Monitors { get; set; } = [];

    public List<HotkeyBinding> Hotkeys { get; set; } = [];

    public bool NotifyOnSwitch { get; set; }

    /// <summary>Pairing with the other computer that shares the monitor.</summary>
    public PeerSettings? Peer { get; set; }

    public MonitorSettings? FindMonitor(string? monitorId) =>
        monitorId is null ? null : Monitors.FirstOrDefault(m => string.Equals(m.MonitorId, monitorId, StringComparison.OrdinalIgnoreCase));

    public MonitorSettings GetOrAddMonitor(string monitorId)
    {
        var existing = FindMonitor(monitorId);
        if (existing is not null)
        {
            return existing;
        }
        var created = new MonitorSettings { MonitorId = monitorId };
        Monitors.Add(created);
        return created;
    }
}

public sealed class MonitorSettings
{
    /// <summary><see cref="Monitors.MonitorIdentity.StableId"/> of the monitor.</summary>
    public string MonitorId { get; set; } = "";

    /// <summary>Optional friendly name for the monitor itself.</summary>
    public string? Label { get; set; }

    /// <summary>Force a specific profile id instead of automatic matching.</summary>
    public string? ProfileId { get; set; }

    /// <summary>The input this computer is plugged into (used by toggle and by the setup wizard).</summary>
    public string? LocalInputId { get; set; }

    /// <summary>The two inputs the toggle hotkey switches between.</summary>
    public List<string>? ToggleInputs { get; set; }

    /// <summary>Friendly names per input id, e.g. { "usbc": "Mac", "hdmi2": "PC" }.</summary>
    public Dictionary<string, string> InputLabels { get; set; } = [];

    public string? LabelFor(string inputId) =>
        InputLabels.FirstOrDefault(kv => string.Equals(kv.Key, inputId, StringComparison.OrdinalIgnoreCase)) is { Value: { Length: > 0 } value }
            ? value
            : null;
}

public enum HotkeyAction
{
    SwitchInput,
    Toggle,
    BrightnessUp,
    BrightnessDown,
    VolumeUp,
    VolumeDown,
}

public static class HotkeyActions
{
    public static readonly HotkeyAction[] ControlActions =
        [HotkeyAction.BrightnessUp, HotkeyAction.BrightnessDown, HotkeyAction.VolumeUp, HotkeyAction.VolumeDown];

    /// <summary>For brightness and volume actions: what to change and by how much.</summary>
    public static (Controls.MonitorControl Control, Controls.ControlChange Change)? ControlChange(this HotkeyAction action) => action switch
    {
        HotkeyAction.BrightnessUp => (Controls.MonitorControl.Brightness, Controls.ControlChange.Adjust(Controls.ControlChange.Step)),
        HotkeyAction.BrightnessDown => (Controls.MonitorControl.Brightness, Controls.ControlChange.Adjust(-Controls.ControlChange.Step)),
        HotkeyAction.VolumeUp => (Controls.MonitorControl.Volume, Controls.ControlChange.Adjust(Controls.ControlChange.Step)),
        HotkeyAction.VolumeDown => (Controls.MonitorControl.Volume, Controls.ControlChange.Adjust(-Controls.ControlChange.Step)),
        _ => null,
    };

    public static string DisplayName(this HotkeyAction action) => action switch
    {
        HotkeyAction.SwitchInput => "Switch input",
        HotkeyAction.Toggle => "Toggle",
        HotkeyAction.BrightnessUp => "Brighter",
        HotkeyAction.BrightnessDown => "Dimmer",
        HotkeyAction.VolumeUp => "Louder",
        HotkeyAction.VolumeDown => "Quieter",
        _ => action.ToString(),
    };
}

/// <summary>Pairing with the other computer (settings.json "peer"). Mirrors <c>PeerSettings</c> in Storage.swift.</summary>
public sealed class PeerSettings
{
    public const int DefaultPort = 47823;

    /// <summary>The same code on both computers, e.g. "K7QF-2M9X-HC4T-8WPA-R3NB". Empty means not paired.</summary>
    public string? PairingCode { get; set; }

    /// <summary>UDP port both computers listen on.</summary>
    public int? Port { get; set; }

    public int EffectivePort => Port is >= 1024 and <= 65535 ? Port.Value : DefaultPort;

    /// <summary>The key for this pairing, or null when not paired or the code is not valid.</summary>
    public Peer.PeerKey? Key => PairingCode is null ? null : Peer.PeerKey.FromPairingCode(PairingCode);
}

public sealed class HotkeyBinding
{
    public string MonitorId { get; set; } = "";

    public HotkeyAction Action { get; set; }

    /// <summary>Input id for <see cref="HotkeyAction.SwitchInput"/>.</summary>
    public string? InputId { get; set; }

    /// <summary>Key combination such as "Ctrl+Alt+F1". Parsed by the platform layer.</summary>
    public string Keys { get; set; } = "";
}
