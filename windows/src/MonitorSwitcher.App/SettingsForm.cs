using System.Text.Json;
using MonitorSwitcher.Core;
using MonitorSwitcher.Core.Controls;
using MonitorSwitcher.Core.Peer;
using MonitorSwitcher.Core.Profiles;
using MonitorSwitcher.Core.Storage;
using MonitorSwitcher.Core.Switching;

namespace MonitorSwitcher.App;

/// <summary>Friendly input names, shortcuts, toggle pair, start at login and notifications.</summary>
internal sealed class SettingsForm : Form
{
    private readonly AppServices _services;
    private readonly IReadOnlyList<ResolvedMonitor> _monitors;
    private readonly AppSettings _working;
    private readonly Func<AppSettings, bool, IReadOnlyList<string>> _apply;
    private readonly Action<ResolvedMonitor?> _openWizard;
    private readonly ComboBox _monitorPicker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.Px(380) };
    private readonly Label _monitorName = Ui.Text("");
    private readonly Label _monitorInfo = Ui.Text("", muted: true);
    private readonly Label _monitorNote = Ui.Text("", muted: true);
    private readonly TableLayoutPanel _inputs = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 4, Margin = Padding.Empty };
    private readonly ComboBox _toggleA = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.Px(150) };
    private readonly ComboBox _toggleB = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.Px(150) };
    private readonly HotkeyBox _toggleHotkey = new();
    private readonly CheckBox _startAtLogin = new() { Text = "Start Monitor Switcher when I sign in", AutoSize = true };
    private readonly CheckBox _notify = new() { Text = "Show a notification after each switch", AutoSize = true };
    private readonly ComboBox _defaultMonitor = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.Px(380) };
    private readonly Label _status = Ui.Text("");
    private readonly Dictionary<HotkeyAction, HotkeyBox> _controlHotkeys = HotkeyActions.ControlActions.ToDictionary(a => a, _ => new HotkeyBox());
    private readonly TextBox _pairingCode = new() { Width = Ui.Px(260), PlaceholderText = "Same code on both computers", Font = new Font(FontFamily.GenericMonospace, 10f) };
    private readonly Label _peerStatus = Ui.Text("", muted: true);
    private readonly Func<string, Task<string>> _testPeer;
    private readonly List<InputRow> _rows = [];
    private ResolvedMonitor? _current;
    private bool _toggleTouched;
    private bool _loading;

    private sealed record InputRow(ProfileInput Input, TextBox Label, HotkeyBox Hotkey, RadioButton ThisComputer);

    private sealed record Choice(string Id, string Text)
    {
        public override string ToString() => Text;
    }

    public SettingsForm(
        AppServices services,
        IReadOnlyList<ResolvedMonitor> monitors,
        AppSettings settings,
        Func<AppSettings, bool, IReadOnlyList<string>> apply,
        Action<ResolvedMonitor?> openWizard,
        Func<string, Task<string>> testPeer,
        bool welcome)
    {
        _services = services;
        _testPeer = testPeer;
        _monitors = monitors;
        _apply = apply;
        _openWizard = openWizard;
        _working = JsonSerializer.Deserialize(JsonSerializer.Serialize(settings, CoreJsonContext.Default.AppSettings), CoreJsonContext.Default.AppSettings)!;

        SuspendLayout();
        Ui.ApplyFormDefaults(this, "Monitor Switcher Settings");

        var root = Ui.Root();
        root.Controls.Add(new Label { Text = "Settings", AutoSize = true, Font = Ui.Title(Font), Margin = Ui.Pad(0, 0, 0, 4) });

        if (welcome)
        {
            root.Controls.Add(Ui.Text("Welcome! Monitor Switcher lives in the system tray. Name your inputs, tell it which input this computer is plugged into, and pick shortcuts. You can come back here any time from the tray icon."));
        }

        if (monitors.Count == 0)
        {
            root.Controls.Add(Ui.SectionHeader(Font, "No monitors found"));
            root.Controls.Add(Ui.Text("No monitor that supports DDC/CI was found. Make sure DDC/CI is turned on in the monitor's menu, then run 'monitorswitcher --diagnose' in a terminal for details.", muted: true));
        }
        else
        {
            root.Controls.Add(Ui.SectionHeader(Font, "Monitor"));
            if (monitors.Count > 1)
            {
                foreach (var m in monitors)
                {
                    _monitorPicker.Items.Add(new Choice(m.StableId, $"{_working.FindMonitor(m.StableId)?.Label ?? m.Name}  ({m.OsDisplayName ?? m.StableId})"));
                }
                _monitorPicker.SelectedIndexChanged += (_, _) => LoadMonitor(monitors[_monitorPicker.SelectedIndex]);
                root.Controls.Add(_monitorPicker);
            }
            else
            {
                _monitorName.Font = new Font(Font, FontStyle.Bold);
                root.Controls.Add(_monitorName);
            }
            root.Controls.Add(_monitorInfo);
            root.Controls.Add(_monitorNote);

            root.Controls.Add(Ui.SectionHeader(Font, "Inputs"));
            root.Controls.Add(_inputs);

            root.Controls.Add(Ui.SectionHeader(Font, "Toggle"));
            var toggleRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            toggleRow.Controls.Add(new Label { Text = "Switch between", AutoSize = true, Anchor = AnchorStyles.Left, Margin = Ui.Pad(0, 6, 6, 0) });
            toggleRow.Controls.Add(_toggleA);
            toggleRow.Controls.Add(new Label { Text = "and", AutoSize = true, Margin = Ui.Pad(6, 6, 6, 0) });
            toggleRow.Controls.Add(_toggleB);
            root.Controls.Add(toggleRow);
            var toggleKeyRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Ui.Pad(0, 6, 0, 0) };
            toggleKeyRow.Controls.Add(new Label { Text = "Shortcut", AutoSize = true, Margin = Ui.Pad(0, 6, 6, 0) });
            toggleKeyRow.Controls.Add(_toggleHotkey);
            root.Controls.Add(toggleKeyRow);

            root.Controls.Add(Ui.SectionHeader(Font, "Brightness and volume"));
            var levels = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = Padding.Empty };
            int levelRow = 0;
            foreach (var (action, box) in _controlHotkeys)
            {
                levels.Controls.Add(new Label { Text = action.DisplayName(), AutoSize = true, Anchor = AnchorStyles.Left, Margin = Ui.Pad(0, 6, 12, 4) }, 0, levelRow);
                box.Margin = Ui.Pad(0, 4, 0, 4);
                levels.Controls.Add(box, 1, levelRow++);
            }
            root.Controls.Add(levels);
            root.Controls.Add(Ui.Text($"Each press changes the level by {ControlChange.Step}%. Levels are also in the tray menu.", muted: true));
        }

        root.Controls.Add(Ui.SectionHeader(Font, "Other computer"));
        root.Controls.Add(Ui.Text("If your monitor only obeys the computer it is showing, pair this PC with the other computer that runs Monitor Switcher. Then either computer can switch the monitor, and change its brightness and volume, whichever one it is showing.", muted: true));
        _pairingCode.Text = _working.Peer?.PairingCode ?? "";
        var pairRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Ui.Pad(0, 6, 0, 0) };
        pairRow.Controls.Add(new Label { Text = "Pairing code", AutoSize = true, Margin = Ui.Pad(0, 6, 6, 0) });
        pairRow.Controls.Add(_pairingCode);
        pairRow.Controls.Add(Ui.Button("New code", (_, _) =>
        {
            _pairingCode.Text = PairingCode.Generate();
            _peerStatus.Text = "Type this code into Monitor Switcher on the other computer, then Save on both.";
        }));
        pairRow.Controls.Add(Ui.Button("Copy", (_, _) =>
        {
            if (_pairingCode.Text.Length > 0)
            {
                Clipboard.SetText(_pairingCode.Text);
            }
        }));
        root.Controls.Add(pairRow);
        var testRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Ui.Pad(0, 6, 0, 0) };
        Button? testButton = null;
        testButton = Ui.Button("Test connection", async (_, _) =>
        {
            testButton!.Enabled = false;
            _peerStatus.Text = "Contacting the other computer...";
            _peerStatus.Text = await _testPeer(_pairingCode.Text);
            testButton.Enabled = true;
        });
        testRow.Controls.Add(testButton);
        root.Controls.Add(testRow);
        root.Controls.Add(_peerStatus);

        root.Controls.Add(Ui.SectionHeader(Font, "General"));
        _startAtLogin.Checked = StartupRegistration.IsEnabled() || welcome;
        _notify.Checked = _working.NotifyOnSwitch;
        root.Controls.Add(_startAtLogin);
        root.Controls.Add(_notify);
        if (monitors.Count > 1)
        {
            root.Controls.Add(Ui.Text("Monitor used when a command doesn't name one:", muted: true));
            foreach (var m in monitors)
            {
                _defaultMonitor.Items.Add(new Choice(m.StableId, _working.FindMonitor(m.StableId)?.Label ?? m.Name));
            }
            _defaultMonitor.SelectedIndex = Math.Max(0, monitors.ToList().FindIndex(m => m.StableId == _working.DefaultMonitorId));
            root.Controls.Add(_defaultMonitor);
        }

        _status.Margin = Ui.Pad(0, 14, 0, 0);
        _status.Visible = false;
        root.Controls.Add(_status);
        _toggleA.SelectedIndexChanged += (_, _) => _toggleTouched |= !_loading;
        _toggleB.SelectedIndexChanged += (_, _) => _toggleTouched |= !_loading;

        var buttons = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = Ui.Pad(0, 12, 0, 0) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var wizardButton = Ui.Button("Run setup wizard...", (_, _) =>
        {
            // The wizard saves settings itself; close so this window can't overwrite them later.
            var monitor = _current;
            Close();
            _openWizard(monitor);
        });
        wizardButton.Margin = Padding.Empty;
        buttons.Controls.Add(wizardButton, 0, 0);
        var right = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        var save = Ui.Button("Save", (_, _) => Save(), primary: true);
        var cancel = Ui.Button("Cancel", (_, _) => Close());
        right.Controls.Add(save);
        right.Controls.Add(cancel);
        buttons.Controls.Add(right, 1, 0);
        root.Controls.Add(buttons);
        AcceptButton = save;
        CancelButton = cancel;

        Controls.Add(root);
        ResumeLayout(false);
        PerformLayout();

        if (monitors.Count > 0)
        {
            if (monitors.Count > 1)
            {
                _monitorPicker.SelectedIndex = 0;
            }
            else
            {
                LoadMonitor(monitors[0]);
            }
        }
    }

    private void LoadMonitor(ResolvedMonitor monitor)
    {
        if (_current is not null)
        {
            StoreMonitor();
        }
        _current = monitor;
        _loading = true;
        _toggleTouched = false;
        var ms = _working.FindMonitor(monitor.StableId);
        _monitorName.Text = monitor.Name;

        var sender = monitor.EndpointFor(monitor.Protocol);
        var origin = monitor.Profile.Origin switch
        {
            ProfileOrigin.BuiltIn => "built-in profile",
            ProfileOrigin.User => "your profile",
            _ => "no profile yet",
        };
        var details = $"{monitor.Profile.Id} ({origin}) - {monitor.Protocol.DisplayName} - via {sender?.Transport.DisplayName ?? "no compatible interface"}";
        _monitorInfo.Text = details;

        _monitorNote.Text = monitor.NeedsSetup
            ? "No profile matched this monitor, so these are standard guesses. Run the setup wizard to find the codes that work."
            : monitor.Profile.EffectiveQuirks.IgnoresCommandsWhenInactive
                ? "This monitor only obeys the computer it is showing. Shortcuts here can switch it away from this computer. To switch back from here too, pair this PC with the other computer below."
                : "";
        _monitorNote.Visible = _monitorNote.Text.Length > 0;

        _inputs.SuspendLayout();
        foreach (Control c in _inputs.Controls.Cast<Control>().ToList())
        {
            c.Dispose();
        }
        _inputs.Controls.Clear();
        _inputs.RowStyles.Clear();
        _rows.Clear();

        AddHeader("Port", 0);
        AddHeader("Your name for it", 1);
        AddHeader("Shortcut", 2);
        AddHeader("This computer", 3);

        int row = 1;
        foreach (var input in monitor.Profile.Inputs)
        {
            var port = new Label
            {
                Text = input.Verified ? $"{input.Name}  ✓" : input.Name,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = Ui.Pad(0, 4, 16, 4),
            };
            var label = new TextBox { Text = ms?.LabelFor(input.Id) ?? "", Width = Ui.Px(150), PlaceholderText = "e.g. Mac or PC", Margin = Ui.Pad(0, 4, 12, 4) };
            var hotkeyBox = new HotkeyBox { Margin = Ui.Pad(0, 4, 12, 4) };
            var binding = _working.Hotkeys.FirstOrDefault(h => h.MonitorId == monitor.StableId && h.Action == HotkeyAction.SwitchInput && string.Equals(h.InputId, input.Id, StringComparison.OrdinalIgnoreCase));
            if (binding is not null && Hotkey.TryParse(binding.Keys, out var hk))
            {
                hotkeyBox.Hotkey = hk;
            }
            var radio = new RadioButton
            {
                AutoSize = true,
                Checked = string.Equals(ms?.LocalInputId, input.Id, StringComparison.OrdinalIgnoreCase),
                Anchor = AnchorStyles.None,
                Margin = Ui.Pad(0, 6, 0, 4),
            };

            var inputIndex = row - 1;
            radio.CheckedChanged += (_, _) =>
            {
                // Picking "this computer" also makes it one end of the toggle, unless the user set the toggle by hand.
                if (radio.Checked && !_loading && !_toggleTouched)
                {
                    SetToggleDefaults(monitor, monitor.Profile.Inputs[inputIndex].Id);
                }
            };

            _inputs.Controls.Add(port, 0, row);
            _inputs.Controls.Add(label, 1, row);
            _inputs.Controls.Add(hotkeyBox, 2, row);
            _inputs.Controls.Add(radio, 3, row);
            new ToolTip().SetToolTip(port, input.Verified ? "Confirmed to work on this model." : "Not yet confirmed on this model.");
            _rows.Add(new InputRow(input, label, hotkeyBox, radio));
            row++;
        }
        _inputs.ResumeLayout();

        _toggleA.Items.Clear();
        _toggleB.Items.Clear();
        foreach (var input in monitor.Profile.Inputs)
        {
            _toggleA.Items.Add(new Choice(input.Id, input.Name));
            _toggleB.Items.Add(new Choice(input.Id, input.Name));
        }
        if (ToggleLogic.ToggleInputs(monitor.Profile, ms) is { } pair)
        {
            _toggleA.SelectedIndex = monitor.Profile.Inputs.FindIndex(i => i.Id == pair.A);
            _toggleB.SelectedIndex = monitor.Profile.Inputs.FindIndex(i => i.Id == pair.B);
        }
        else
        {
            SetToggleDefaults(monitor, ms?.LocalInputId);
        }

        var toggleBinding = _working.Hotkeys.FirstOrDefault(h => h.MonitorId == monitor.StableId && h.Action == HotkeyAction.Toggle);
        _toggleHotkey.Hotkey = toggleBinding is not null && Hotkey.TryParse(toggleBinding.Keys, out var thk) ? thk : null;
        foreach (var (action, box) in _controlHotkeys)
        {
            var controlBinding = _working.Hotkeys.FirstOrDefault(h => h.MonitorId == monitor.StableId && h.Action == action);
            box.Hotkey = controlBinding is not null && Hotkey.TryParse(controlBinding.Keys, out var chk) ? chk : null;
        }
        _loading = false;
    }

    /// <summary>Toggle between this computer's input and the first verified other input.</summary>
    private void SetToggleDefaults(ResolvedMonitor monitor, string? localInputId)
    {
        var inputs = monitor.Profile.Inputs;
        var b = inputs.FindIndex(i => i.Verified && i.Id != localInputId);
        var a = localInputId is null ? -1 : inputs.FindIndex(i => i.Id == localInputId);
        if (b < 0)
        {
            b = inputs.FindIndex(i => i.Id != localInputId);
        }
        if (a < 0 || a == b)
        {
            a = inputs.FindIndex(i => i != inputs[Math.Max(b, 0)]);
        }

        var wasLoading = _loading;
        _loading = true;
        _toggleA.SelectedIndex = Math.Max(a, 0);
        _toggleB.SelectedIndex = Math.Max(b, 0);
        _loading = wasLoading;
    }

    private void AddHeader(string text, int column)
    {
        _inputs.Controls.Add(new Label { Text = text, AutoSize = true, ForeColor = Ui.Muted, Margin = Ui.Pad(0, 0, 12, 2) }, column, 0);
    }

    /// <summary>Copies the visible monitor's controls into the working settings.</summary>
    private void StoreMonitor()
    {
        if (_current is null)
        {
            return;
        }

        var ms = _working.GetOrAddMonitor(_current.StableId);
        ms.InputLabels = _rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Label.Text))
            .ToDictionary(r => r.Input.Id, r => r.Label.Text.Trim());
        ms.LocalInputId = _rows.FirstOrDefault(r => r.ThisComputer.Checked)?.Input.Id;
        ms.ToggleInputs = _toggleA.SelectedItem is Choice a && _toggleB.SelectedItem is Choice b ? [a.Id, b.Id] : null;

        _working.Hotkeys.RemoveAll(h => h.MonitorId == _current.StableId);
        foreach (var row in _rows.Where(r => r.Hotkey.Hotkey is not null))
        {
            _working.Hotkeys.Add(new HotkeyBinding
            {
                MonitorId = _current.StableId,
                Action = HotkeyAction.SwitchInput,
                InputId = row.Input.Id,
                Keys = row.Hotkey.Hotkey!.Value.ToString(),
            });
        }
        if (_toggleHotkey.Hotkey is { } toggle)
        {
            _working.Hotkeys.Add(new HotkeyBinding { MonitorId = _current.StableId, Action = HotkeyAction.Toggle, Keys = toggle.ToString() });
        }
        foreach (var (action, box) in _controlHotkeys)
        {
            if (box.Hotkey is { } hotkey)
            {
                _working.Hotkeys.Add(new HotkeyBinding { MonitorId = _current.StableId, Action = action, Keys = hotkey.ToString() });
            }
        }
    }

    private void Save()
    {
        StoreMonitor();
        _working.NotifyOnSwitch = _notify.Checked;
        if (_defaultMonitor.SelectedItem is Choice def)
        {
            _working.DefaultMonitorId = def.Id;
        }

        var code = _pairingCode.Text.Trim();
        if (code.Length == 0)
        {
            if (_working.Peer is not null)
            {
                _working.Peer.PairingCode = null;
            }
        }
        else if (PairingCode.Normalize(code) is { } normalized)
        {
            _working.Peer ??= new PeerSettings();
            _working.Peer.PairingCode = PairingCode.Format(normalized);
        }
        else
        {
            ShowStatus("The pairing code is not valid. Use \"New code\" and type the same code on the other computer.", error: true);
            return;
        }

        var duplicate = _working.Hotkeys
            .GroupBy(h => h.Keys, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            ShowStatus($"{duplicate.Key} is used for more than one action.", error: true);
            return;
        }
        foreach (var ms in _working.Monitors)
        {
            if (ms.ToggleInputs is [var a, var b] && a == b)
            {
                ShowStatus("Choose two different inputs to toggle between.", error: true);
                return;
            }
        }

        var problems = _apply(_working, _startAtLogin.Checked);
        if (problems.Count > 0)
        {
            ShowStatus(string.Join(" ", problems) + " Your other settings were saved.", error: true);
            return;
        }
        Close();
    }

    private void ShowStatus(string text, bool error)
    {
        _status.Visible = text.Length > 0;
        _status.Text = text;
        _status.ForeColor = error ? Ui.Error : Ui.Success;
    }
}
