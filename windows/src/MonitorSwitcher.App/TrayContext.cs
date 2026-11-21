using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;
using MonitorSwitcher.Core;
using MonitorSwitcher.Core.Controls;
using MonitorSwitcher.Core.Ddc;
using MonitorSwitcher.Core.Logging;
using MonitorSwitcher.Core.Peer;
using MonitorSwitcher.Core.Storage;
using MonitorSwitcher.Core.Switching;
using MonitorSwitcher.Windows.Peer;

namespace MonitorSwitcher.App;

/// <summary>
/// The tray icon, its menu, global hotkeys and notifications. Switching always runs off the UI
/// thread, one switch at a time, so a slow monitor never freezes the menu.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly AppServices _services;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly HotkeyWindow _hotkeyWindow;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly Dictionary<int, HotkeyBinding> _hotkeyIds = [];
    private readonly System.Windows.Forms.Timer _displayChangeDebounce = new() { Interval = 1500 };
    private readonly SynchronizationContext _ui;
    private AppSettings _settings;
    private IReadOnlyList<ResolvedMonitor> _monitors = [];
    private string? _discoveryError;
    private SettingsForm? _settingsForm;
    private SetupWizardForm? _wizardForm;
    private MonitorActions _actions;
    private UdpPeerListener? _listener;
    private (int Port, bool Paired)? _listenerConfig;

    /// <summary>Last known brightness/volume per monitor, in percent.</summary>
    private Dictionary<string, Dictionary<MonitorControl, int>> _levels = [];

    public TrayContext(AppServices services, bool firstRun, bool openWizard)
    {
        _services = services;
        _ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_ui);
        _settings = services.Settings.Load();
        _actions = PeerFactory.Actions(services, _settings);

        _menu = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
        _menu.Opening += (_, e) =>
        {
            BuildMenu();
            e.Cancel = false;
        };

        _tray = new NotifyIcon
        {
            Icon = AppIcons.Tray(),
            Text = "Monitor Switcher",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowMenuAtCursor();
            }
        };

        _hotkeyWindow = new HotkeyWindow(OnHotkey, wizard =>
        {
            if (wizard)
            {
                OpenWizard(null);
            }
            else
            {
                OpenSettings();
            }
        });
        _displayChangeDebounce.Tick += async (_, _) =>
        {
            _displayChangeDebounce.Stop();
            await RefreshMonitorsAsync();
        };
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        ApplyPeerSettings();
        var hotkeyProblems = RegisterHotkeys();
        _ = StartAsync(firstRun, openWizard, hotkeyProblems);
    }

    private async Task StartAsync(bool firstRun, bool openWizard, IReadOnlyList<string> hotkeyProblems)
    {
        await RefreshMonitorsAsync();
        if (openWizard)
        {
            OpenWizard(null);
        }
        else if (firstRun)
        {
            OpenSettings(welcome: true);
        }
        else if (hotkeyProblems.Count > 0)
        {
            Notify("Some shortcuts are unavailable", string.Join(" ", hotkeyProblems), ToolTipIcon.Warning);
        }
        else if (_monitors.Any(m => m.NeedsSetup))
        {
            Notify("New monitor found", "Open the menu and choose \"Set up a monitor\" to find the input codes that work.", ToolTipIcon.Info);
        }
    }

    // ---- Monitors -------------------------------------------------------------------------

    private async Task RefreshMonitorsAsync()
    {
        var settings = _settings;
        try
        {
            _monitors = await Task.Run(() => _services.Switcher.DiscoverMonitors(settings));
            _discoveryError = null;
        }
        catch (DdcException ex)
        {
            _monitors = [];
            _discoveryError = ex.UserMessage;
            _services.Log.Warn($"Monitor discovery failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            _monitors = [];
            _discoveryError = $"Could not list monitors: {ex.Message}";
            _services.Log.Error("Monitor discovery failed.", ex);
        }

        var names = string.Join(", ", _monitors.Select(m => _settings.FindMonitor(m.StableId)?.Label ?? m.Name));
        var tip = names.Length == 0 ? "Monitor Switcher" : $"Monitor Switcher - {names}";
        _tray.Text = tip.Length > 127 ? tip[..124] + "..." : tip;
        await RefreshLevelsAsync();
    }

    /// <summary>Reads brightness and volume of every monitor that answers (off the UI thread).</summary>
    private async Task RefreshLevelsAsync()
    {
        var monitors = _monitors;
        try
        {
            _levels = await Task.Run(() =>
            {
                var result = new Dictionary<string, Dictionary<MonitorControl, int>>();
                foreach (var monitor in monitors)
                {
                    foreach (var control in MonitorControls.All.Where(c => Supports(monitor, c)))
                    {
                        if (_services.Switcher.ReadControl(monitor, control) is { } reading)
                        {
                            if (!result.TryGetValue(monitor.StableId, out var levels))
                            {
                                result[monitor.StableId] = levels = [];
                            }
                            levels[control] = ControlMath.Percent(reading);
                        }
                    }
                }
                return result;
            });
        }
        catch (Exception ex)
        {
            _services.Log.Warn("Reading brightness and volume failed.", ex);
        }
    }

    private static bool Supports(ResolvedMonitor monitor, MonitorControl control) =>
        monitor.Capabilities is not { Vcp.Count: > 0 } caps || caps.SupportsVcp(control.VcpCode());

    private int? Level(ResolvedMonitor monitor, MonitorControl control)
    {
        if (_levels.TryGetValue(monitor.StableId, out var levels) && levels.TryGetValue(control, out var percent))
        {
            return percent;
        }
        return _services.State.Load().Monitors.TryGetValue(monitor.StableId, out var state)
            && state.Levels.TryGetValue(control.Name(), out var last) ? last : null;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Raised several times per change, possibly off the UI thread: debounce on the UI thread.
        _ui.Post(_ =>
        {
            _displayChangeDebounce.Stop();
            _displayChangeDebounce.Start();
        }, null);
    }

    private ResolvedMonitor? FindMonitor(string stableId) => _monitors.FirstOrDefault(m => m.StableId == stableId);

    // ---- Switching ------------------------------------------------------------------------

    private void OnHotkey(int id)
    {
        if (_hotkeyIds.TryGetValue(id, out var binding))
        {
            _services.Log.Debug($"Hotkey {binding.Keys} pressed.");
            _ = ExecuteAsync(binding.MonitorId, binding.Action, binding.InputId);
        }
    }

    private Task ExecuteAsync(string monitorId, HotkeyAction action, string? inputId)
    {
        if (action.ControlChange() is { } control)
        {
            return ChangeLevelAsync(monitorId, control.Control, control.Change);
        }
        return RunAsync(monitorId, (actions, monitor, ms) => action == HotkeyAction.Toggle
            ? actions.Toggle(monitor, ms)
            : actions.SwitchInput(monitor, inputId!, ms));
    }

    private Task ChangeLevelAsync(string monitorId, MonitorControl control, ControlChange change) =>
        RunAsync(monitorId, (actions, monitor, _) => actions.SetControl(monitor, control, change));

    /// <summary>Runs one action off the UI thread, one at a time, and reports the result.</summary>
    private async Task RunAsync(string monitorId, Func<MonitorActions, ResolvedMonitor, MonitorSettings?, ActionResult> body)
    {
        if (!await _busy.WaitAsync(0))
        {
            _services.Log.Debug("An action is already running; ignored.");
            return;
        }

        var settings = _settings;
        var actions = _actions;
        try
        {
            var result = await Task.Run(() =>
            {
                var monitor = FindOrDiscover(settings, monitorId);
                return body(actions, monitor, settings.FindMonitor(monitorId));
            });
            if (result.Control is { } control)
            {
                if (result.Percent is { } percent)
                {
                    if (!_levels.TryGetValue(monitorId, out var levels))
                    {
                        _levels[monitorId] = levels = [];
                    }
                    levels[control] = percent;
                }
            }
            else if (settings.NotifyOnSwitch)
            {
                var ms = settings.FindMonitor(monitorId);
                var name = result.InputName ?? result.InputId ?? "?";
                var label = ms?.LabelFor(result.InputId ?? "");
                var text = label is null ? $"Switched to {name}" : $"Switched to {label} ({name})";
                Notify(ms?.Label ?? result.MonitorName, result.ViaPeer is null ? text : $"{text} by {result.ViaPeer}", ToolTipIcon.None);
            }
        }
        catch (DdcException ex)
        {
            _services.Log.Warn($"Action failed: {ex.Message}");
            var title = ex.Kind switch
            {
                DdcErrorKind.MonitorOnOtherInput => "Monitor is showing another input",
                DdcErrorKind.PeerUnavailable => "The other computer did not respond",
                _ => "Could not do that",
            };
            Notify(title, ex.UserMessage, ex.Kind == DdcErrorKind.MonitorOnOtherInput ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            _services.Log.Error("Action failed unexpectedly.", ex);
            Notify("Could not do that", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>Runs on a worker thread.</summary>
    private ResolvedMonitor FindOrDiscover(AppSettings settings, string monitorId)
    {
        var monitor = FindMonitor(monitorId);
        if (monitor is null)
        {
            // Displays may have changed since the last refresh.
            _monitors = _services.Switcher.DiscoverMonitors(settings);
            monitor = FindMonitor(monitorId) ?? throw new DdcException(
                DdcErrorKind.MonitorNotFound,
                $"The monitor for this shortcut ({monitorId}) is not connected.");
        }
        return monitor;
    }

    // ---- Other computer -----------------------------------------------------------------------

    /// <summary>Rebuilds the actions (with or without relaying) and starts, restarts or stops the listener.</summary>
    private void ApplyPeerSettings()
    {
        _actions = PeerFactory.Actions(_services, _settings);
        var paired = _settings.Peer?.Key is not null;
        var port = _settings.Peer?.EffectivePort ?? PeerSettings.DefaultPort;
        if (_listenerConfig == (port, paired))
        {
            return;
        }

        _listener?.Dispose();
        _listener = null;
        _listenerConfig = (port, paired);
        if (!paired)
        {
            return;
        }

        var actions = _actions;
        var store = _services.Settings;
        var listener = new UdpPeerListener(port, actions.Machine, _services.Log, () => store.Load().Peer?.Key, request =>
        {
            var reply = actions.Handle(request, store.Load());
            _ui.Post(_ => PeerHandled(request, reply), null);
            return reply;
        });
        try
        {
            listener.Start();
            _listener = listener;
        }
        catch (DdcException ex)
        {
            listener.Dispose();
            _services.Log.Warn($"Could not listen for the other computer: {ex.Message}");
            Notify("Pairing is not working", ex.UserMessage, ToolTipIcon.Warning);
        }
    }

    private void PeerHandled(PeerRequest request, PeerReply reply)
    {
        if (request.Kind == PeerRequestKind.Control && reply.Percent is { } percent
            && MonitorControls.Named(request.Control) is { } control && request.Monitor?.Find(_monitors) is { } monitor)
        {
            if (!_levels.TryGetValue(monitor.StableId, out var levels))
            {
                _levels[monitor.StableId] = levels = [];
            }
            levels[control] = percent;
        }
        if (request.Kind == PeerRequestKind.Switch && reply.Ok && _settings.NotifyOnSwitch)
        {
            Notify($"Switched by {request.From}", reply.Message, ToolTipIcon.None);
        }
    }

    /// <summary>Asks the other computer to answer, using a pairing code that may not be saved yet.</summary>
    private async Task<string> TestPeerAsync(string code)
    {
        if (PeerKey.FromPairingCode(code) is not { } key)
        {
            return "That pairing code is not valid. Use \"New code\" on one computer and type the same code on the other.";
        }
        var port = _settings.Peer?.EffectivePort ?? PeerSettings.DefaultPort;
        var machine = _actions.Machine;
        try
        {
            var reply = await Task.Run(() =>
                new MonitorActions(_services, machine, new UdpPeerChannel(key, port, _services.State, _services.Log)).Ping());
            var address = _services.State.Load().Peer?.LastAddress;
            return $"Connected: {reply.From}{(address is null ? "" : $" at {address}")} answered.";
        }
        catch (DdcException ex)
        {
            return ex.UserMessage;
        }
    }

    // ---- Hotkeys --------------------------------------------------------------------------

    private List<string> RegisterHotkeys()
    {
        _hotkeyWindow.UnregisterAll();
        _hotkeyIds.Clear();
        var problems = new List<string>();
        int id = 1;
        foreach (var binding in _settings.Hotkeys)
        {
            if (!Hotkey.TryParse(binding.Keys, out var hotkey))
            {
                problems.Add($"\"{binding.Keys}\" is not a valid shortcut.");
                continue;
            }
            if (_hotkeyWindow.TryRegister(id, hotkey))
            {
                _hotkeyIds[id++] = binding;
            }
            else
            {
                problems.Add($"{hotkey} is already used by another app.");
            }
        }

        foreach (var problem in problems)
        {
            _services.Log.Warn($"Hotkey: {problem}");
        }
        return problems;
    }

    private string? HotkeyText(string monitorId, HotkeyAction action, string? inputId) =>
        _settings.Hotkeys.FirstOrDefault(h =>
            h.MonitorId == monitorId && h.Action == action
            && (action != HotkeyAction.SwitchInput || string.Equals(h.InputId, inputId, StringComparison.OrdinalIgnoreCase)))?.Keys;

    // ---- Menu -----------------------------------------------------------------------------

    private void BuildMenu()
    {
        _menu.SuspendLayout();
        foreach (ToolStripItem item in _menu.Items.Cast<ToolStripItem>().ToList())
        {
            item.Dispose();
        }
        _menu.Items.Clear();

        if (_discoveryError is not null)
        {
            _menu.Items.Add(Disabled(_discoveryError.Length > 120 ? _discoveryError[..117] + "..." : _discoveryError));
            _menu.Items.Add(new ToolStripSeparator());
        }
        else if (_monitors.Count == 0)
        {
            _menu.Items.Add(Disabled("No DDC/CI monitors found"));
            _menu.Items.Add(new ToolStripSeparator());
        }

        var state = _services.State.Load();
        foreach (var monitor in _monitors)
        {
            var ms = _settings.FindMonitor(monitor.StableId);
            state.Monitors.TryGetValue(monitor.StableId, out var monitorState);

            var header = Disabled(ms?.Label ?? monitor.Name);
            header.Font = new Font(_menu.Font, FontStyle.Bold);
            _menu.Items.Add(header);

            foreach (var input in monitor.Profile.Inputs)
            {
                var label = ms?.LabelFor(input.Id);
                var inputId = input.Id;
                var item = new ToolStripMenuItem(label is null ? input.Name : $"{label}  ({input.Name})")
                {
                    Checked = string.Equals(monitorState?.LastInputId, input.Id, StringComparison.OrdinalIgnoreCase),
                    ShortcutKeyDisplayString = HotkeyText(monitor.StableId, HotkeyAction.SwitchInput, input.Id),
                    ToolTipText = input.Verified ? "Confirmed to work on this model" : "Not yet confirmed on this model",
                };
                item.Click += (_, _) => _ = ExecuteAsync(monitor.StableId, HotkeyAction.SwitchInput, inputId);
                _menu.Items.Add(item);
            }

            if (ToggleLogic.ToggleInputs(monitor.Profile, ms) is { } pair)
            {
                string Name(string id) => ms?.LabelFor(id) ?? monitor.Profile.FindInput(id)?.Name ?? id;
                var toggle = new ToolStripMenuItem($"Toggle  ({Name(pair.A)} / {Name(pair.B)})")
                {
                    ShortcutKeyDisplayString = HotkeyText(monitor.StableId, HotkeyAction.Toggle, null),
                };
                toggle.Click += (_, _) => _ = ExecuteAsync(monitor.StableId, HotkeyAction.Toggle, null);
                _menu.Items.Add(toggle);
            }

            foreach (var control in MonitorControls.All.Where(c => Supports(monitor, c)))
            {
                _menu.Items.Add(LevelMenu(monitor, control));
            }

            if (monitor.NeedsSetup)
            {
                var setup = new ToolStripMenuItem("Set up this monitor...");
                setup.Click += (_, _) => OpenWizard(monitor);
                _menu.Items.Add(setup);
            }
            _menu.Items.Add(new ToolStripSeparator());
        }

        _menu.Items.Add(new ToolStripMenuItem("Set up a monitor...", null, (_, _) => OpenWizard(null)));
        _menu.Items.Add(new ToolStripMenuItem("Settings...", null, (_, _) => OpenSettings()));
        _menu.Items.Add(new ToolStripMenuItem("Open log folder", null, (_, _) => OpenLogFolder()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));
        _menu.ResumeLayout();
    }

    private static ToolStripMenuItem Disabled(string text) => new(text) { Enabled = false };

    /// <summary>"Brightness  40%" submenu: step up/down (with shortcuts) and fixed levels.</summary>
    private ToolStripMenuItem LevelMenu(ResolvedMonitor monitor, MonitorControl control)
    {
        var current = Level(monitor, control);
        var menu = new ToolStripMenuItem(current is { } c ? $"{control.Name()}  {c}%" : control.Name());
        if (menu.DropDown is ToolStripDropDownMenu dropDown)
        {
            dropDown.ShowCheckMargin = true;
            dropDown.ShowImageMargin = false;
        }
        var (up, down) = control == MonitorControl.Brightness
            ? (HotkeyAction.BrightnessUp, HotkeyAction.BrightnessDown)
            : (HotkeyAction.VolumeUp, HotkeyAction.VolumeDown);
        foreach (var action in new[] { up, down })
        {
            var step = new ToolStripMenuItem(action.DisplayName())
            {
                ShortcutKeyDisplayString = HotkeyText(monitor.StableId, action, null),
            };
            step.Click += (_, _) => _ = ExecuteAsync(monitor.StableId, action, null);
            menu.DropDownItems.Add(step);
        }
        menu.DropDownItems.Add(new ToolStripSeparator());
        for (int percent = 100; percent >= 0; percent -= 10)
        {
            var value = percent;
            var item = new ToolStripMenuItem($"{value}%")
            {
                Checked = current is { } now && (int)Math.Round(now / 10.0, MidpointRounding.AwayFromZero) * 10 == value,
            };
            item.Click += (_, _) => _ = ChangeLevelAsync(monitor.StableId, control, ControlChange.Set(value));
            menu.DropDownItems.Add(item);
        }
        return menu;
    }

    private void ShowMenuAtCursor()
    {
        // NotifyIcon only exposes right-click menus; its private ShowContextMenu positions the
        // menu correctly and dismisses it when focus moves away.
        typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(_tray, null);
    }

    // ---- Windows --------------------------------------------------------------------------

    private void OpenSettings(bool welcome = false)
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_services, _monitors, _settings, ApplySettings, OpenWizard, TestPeerAsync, welcome);
        _settingsForm.FormClosed += (_, _) =>
        {
            _settingsForm = null;
            MemoryTrim.AfterWindowClosed();
        };
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    private IReadOnlyList<string> ApplySettings(AppSettings updated, bool startAtLogin)
    {
        var problems = new List<string>();
        _services.Settings.Save(updated);
        _settings = updated;
        ApplyPeerSettings();
        try
        {
            StartupRegistration.Set(startAtLogin);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            problems.Add($"Could not change the start-at-login setting: {ex.Message}");
        }
        problems.AddRange(RegisterHotkeys());
        _ = RefreshMonitorsAsync();
        _services.Log.Info($"Settings saved ({updated.Hotkeys.Count} shortcut(s)).");
        return problems;
    }

    private void OpenWizard(ResolvedMonitor? monitor)
    {
        if (_wizardForm is { IsDisposed: false })
        {
            _wizardForm.Activate();
            return;
        }

        _wizardForm = new SetupWizardForm(_services, _monitors, monitor, _settings, onSaved: async () =>
        {
            _settings = _services.Settings.Load();
            ApplyPeerSettings();
            RegisterHotkeys();
            await RefreshMonitorsAsync();
        });
        _wizardForm.FormClosed += (_, _) =>
        {
            _wizardForm = null;
            MemoryTrim.AfterWindowClosed();
        };
        _wizardForm.Show();
        _wizardForm.Activate();
    }

    private void OpenLogFolder()
    {
        Directory.CreateDirectory(_services.Paths.LogsDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_services.Paths.LogsDirectory}\"") { UseShellExecute = true });
    }

    private void Notify(string title, string text, ToolTipIcon icon)
    {
        _tray.ShowBalloonTip(4000, title, text.Length > 250 ? text[..247] + "..." : text, icon);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _displayChangeDebounce.Dispose();
            _hotkeyWindow.Dispose();
            _listener?.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _menu.Dispose();
            _settingsForm?.Dispose();
            _wizardForm?.Dispose();
            _busy.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Gives memory back to Windows after a window closes, so the idle tray app stays small.</summary>
internal static class MemoryTrim
{
    public static void AfterWindowClosed()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }
}
