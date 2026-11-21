using MonitorSwitcher.Core;
using MonitorSwitcher.Core.Ddc;
using MonitorSwitcher.Core.Logging;
using MonitorSwitcher.Core.Profiles;
using MonitorSwitcher.Core.Setup;
using MonitorSwitcher.Core.Storage;
using MonitorSwitcher.Core.Switching;

namespace MonitorSwitcher.App;

/// <summary>
/// Finds the input codes that work on a monitor. Each test switches to the chosen input, counts
/// down, always sends the switch back, and then asks what happened. Some monitors (like the
/// LG 32UN880) ignore the switch back because they only obey the computer they are showing; the
/// wizard notices the monitor going quiet on DDC/CI, waits for the user to return with the
/// monitor's buttons, and records that quirk.
/// </summary>
internal sealed class SetupWizardForm : Form
{
    private const int CountdownSeconds = 8;

    /// <summary>
    /// If the monitor answers again within this time after the switch-back, this computer brought
    /// it back. Returning with the monitor's buttons takes longer (live test: 4.9 s), and a monitor
    /// that obeys typically answers in under two seconds.
    /// </summary>
    private static readonly TimeSpan QuickReturn = TimeSpan.FromSeconds(2.5);

    private readonly AppServices _services;
    private readonly IReadOnlyList<ResolvedMonitor> _monitors;
    private readonly Func<Task> _onSaved;
    private readonly AppSettings _settings;
    private readonly Label _title = new() { AutoSize = true, Margin = Ui.Pad(0, 0, 0, 8) };
    private readonly TableLayoutPanel _page = Ui.Stack();
    private readonly FlowLayoutPanel _buttons = new() { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Ui.Pad(0, 16, 0, 0), WrapContents = false };

    private ResolvedMonitor? _monitor;
    private PortChoice? _local;
    private PortChoice? _target;
    private string _targetLabel = "";
    private IReadOnlyList<SetupAttempt> _attempts = [];
    private int _attemptIndex;

    private CancellationTokenSource? _testCancel;
    private volatile bool _endCountdownEarly;
    private bool _testing;
    private bool _closeAfterTest;

    // Evidence from the current attempt.
    private bool _wentSilent;
    private bool? _cameBackQuickly;

    // Accepted result.
    private SetupAttempt? _result;
    private bool? _ignoresCommandsWhenInactive;
    private bool _returnVerified;

    public SetupWizardForm(AppServices services, IReadOnlyList<ResolvedMonitor> monitors, ResolvedMonitor? monitor, AppSettings settings, Func<Task> onSaved)
    {
        _services = services;
        _monitors = monitors;
        _settings = settings;
        _onSaved = onSaved;

        SuspendLayout();
        Ui.ApplyFormDefaults(this, "Set up a monitor");
        KeyPreview = true;
        KeyDown += (_, _) =>
        {
            if (_testing)
            {
                _endCountdownEarly = true;
            }
        };
        _title.Font = Ui.Title(Font);
        _page.MinimumSize = Ui.Sz(Ui.ContentWidth, 0);

        var root = Ui.Root();
        root.Controls.Add(_title);
        root.Controls.Add(_page);
        root.Controls.Add(_buttons);
        Controls.Add(root);
        ResumeLayout(false);
        PerformLayout();

        if (monitor is not null)
        {
            ShowInputsPage(monitor);
        }
        else if (monitors.Count == 1)
        {
            ShowInputsPage(monitors[0]);
        }
        else if (monitors.Count == 0)
        {
            ShowNoMonitorsPage();
        }
        else
        {
            ShowChooseMonitorPage();
        }
    }

    // ---- Pages ----------------------------------------------------------------------------

    private void ShowNoMonitorsPage()
    {
        SetPage("No monitors found",
            Ui.Text("No monitor that supports DDC/CI was found."),
            Ui.Text(DdcHints.DdcCiDisabled, muted: true),
            Ui.Text("For details, run 'monitorswitcher --diagnose' in a terminal.", muted: true));
        SetButtons(Ui.Button("Close", (_, _) => Close(), primary: true));
    }

    private void ShowChooseMonitorPage()
    {
        var radios = _monitors.Select((m, i) => new RadioButton
        {
            Text = $"{_settings.FindMonitor(m.StableId)?.Label ?? m.Name}   ({m.OsDisplayName ?? m.StableId})",
            AutoSize = true,
            Checked = i == 0,
            Tag = m,
            Margin = Ui.Pad(0, 4, 0, 4),
        }).ToArray();

        var controls = new List<Control> { Ui.Text("Which monitor do you want to set up?") };
        controls.AddRange(radios);
        controls.Add(Ui.Text(TransportSummary(), muted: true));
        SetPage("Set up a monitor", controls.ToArray());
        SetButtons(
            Ui.Button("Next", (_, _) => ShowInputsPage((ResolvedMonitor)radios.First(r => r.Checked).Tag!), primary: true),
            Ui.Button("Cancel", (_, _) => Close()));
    }

    private void ShowInputsPage(ResolvedMonitor monitor)
    {
        _monitor = monitor;
        var ms = _settings.FindMonitor(monitor.StableId);
        var ports = SetupPlanner.PortChoices(monitor);

        var localBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.Px(220) };
        var targetBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.Px(220) };
        localBox.Items.AddRange(ports.Cast<object>().ToArray());
        targetBox.Items.AddRange(ports.Cast<object>().ToArray());
        localBox.SelectedIndex = ports.ToList().FindIndex(p => p.InputId == (_local?.InputId ?? ms?.LocalInputId));
        var targetIndex = ports.ToList().FindIndex(p => p.InputId == _target?.InputId);
        if (targetIndex < 0)
        {
            targetIndex = ports.ToList().FindIndex(p => p.InputId != ms?.LocalInputId && monitor.Profile.FindInput(p.InputId)?.Verified == true);
        }
        targetBox.SelectedIndex = targetIndex;
        var labelBox = new TextBox { Width = Ui.Px(220), PlaceholderText = "e.g. Mac" };
        targetBox.SelectedIndexChanged += (_, _) =>
        {
            if (targetBox.SelectedItem is PortChoice t)
            {
                labelBox.Text = ms?.LabelFor(t.InputId) ?? "";
            }
        };
        if (targetBox.SelectedItem is PortChoice initial)
        {
            labelBox.Text = ms?.LabelFor(initial.InputId) ?? "";
        }

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = Ui.Pad(0, 8, 0, 8) };
        AddRow(grid, "This computer is plugged into", localBox);
        AddRow(grid, "Test switching to", targetBox);
        AddRow(grid, "Your name for that input (optional)", labelBox);

        var status = Ui.Text("");
        status.ForeColor = Ui.Error;

        var known = monitor.NeedsSetup
            ? $"{monitor.Name} has no profile yet, so the wizard will try {string.Join(" and then ", SetupPlanner.UsableProtocols(monitor).Select(p => p.DisplayName))}."
            : $"{monitor.Name} is a known model ({monitor.Profile.Id}). The wizard will confirm its codes on your setup.";

        SetPage("Which inputs?",
            Ui.Text(known),
            grid,
            Ui.SectionHeader(Font, "How the test works"),
            Ui.Text($"1. The monitor switches to the input you chose for {CountdownSeconds} seconds. Make sure the device on that input is on, so you can see whether it worked."),
            Ui.Text("2. Monitor Switcher then switches it back to this computer."),
            Ui.Text("3. If the screen stays on the other input, use the monitor's own buttons to come back. Some monitors only obey the computer they are showing; the wizard detects this and waits for you.", muted: true),
            status);

        SetButtons(
            Ui.Button("Start test", async (_, _) =>
            {
                if (localBox.SelectedItem is not PortChoice local || targetBox.SelectedItem is not PortChoice target)
                {
                    status.Text = "Choose both inputs.";
                    return;
                }
                if (local.InputId == target.InputId)
                {
                    status.Text = "Choose an input other than the one this computer is plugged into.";
                    return;
                }

                _local = local;
                _target = target;
                _targetLabel = labelBox.Text.Trim();
                _attempts = SetupPlanner.Plan(monitor, local.InputId, target.InputId);
                _attemptIndex = 0;
                if (_attempts.Count == 0)
                {
                    status.Text = $"There is nothing to try for {target.Name} with the interfaces that reach this monitor.";
                    return;
                }
                await RunAttemptAsync();
            }, primary: true),
            Ui.Button("Cancel", (_, _) => Close()));
    }

    private async Task RunAttemptAsync()
    {
        var monitor = _monitor!;
        var attempt = _attempts[_attemptIndex];
        var status = new Label { AutoSize = true, Font = Ui.Title(Font), Margin = Ui.Pad(0, 8, 0, 8) };
        var detail = Ui.Text($"Trying code {Hex.Format(attempt.Target.Code)} with {attempt.Protocol.DisplayName} ({_attemptIndex + 1} of {_attempts.Count}).", muted: true);
        var hint = Ui.Text("");

        SetPage($"Testing {_target!.Name}", status, detail, hint);
        var stop = Ui.Button("Stop", (_, _) => _testCancel?.Cancel());
        var worked = Ui.Button("It switched", (_, _) => _endCountdownEarly = true, primary: true);
        SetButtons(worked, stop);

        _testCancel?.Dispose();
        _testCancel = new CancellationTokenSource();
        var token = _testCancel.Token;
        _testing = true;
        _endCountdownEarly = false;
        _wentSilent = false;
        _cameBackQuickly = null;
        bool sent = false;
        DdcException? error = null;

        try
        {
            bool? answeredBefore = await Task.Run(() => _services.Switcher.IsAnswering(monitor));

            status.Text = $"Switching to {_target.Name}...";
            await Task.Run(() => _services.Switcher.SendCandidate(monitor, attempt.Protocol, attempt.Target));
            sent = true;

            hint.Text = "Watch the monitor. Press any key on this computer, or click \"It switched\", to come back early.";
            for (int s = CountdownSeconds; s > 0 && !_endCountdownEarly; s--)
            {
                status.Text = $"Switching back in {s}";
                var tick = Task.Delay(1000, token);
                if (answeredBefore == true && !_wentSilent)
                {
                    _wentSilent = await Task.Run(() => _services.Switcher.IsAnswering(monitor)) == false;
                }
                await tick;
            }
        }
        catch (OperationCanceledException)
        {
            // Stop pressed: fall through to the switch back.
        }
        catch (DdcException ex)
        {
            error = ex;
        }
        finally
        {
            if (sent)
            {
                await SwitchBackAsync(attempt, status, hint);
            }
            _testing = false;
        }

        if (_closeAfterTest)
        {
            Close();
            return;
        }
        if (error is not null)
        {
            ShowAttemptError(error);
            return;
        }
        if (token.IsCancellationRequested)
        {
            ShowInputsPage(monitor);
            return;
        }
        ShowQuestionPage();
    }

    private async Task SwitchBackAsync(SetupAttempt attempt, Label status, Label hint)
    {
        var monitor = _monitor!;
        status.Text = $"Switching back to {_local!.Name}...";
        var revertedAt = DateTime.UtcNow;
        if (attempt.Return is { } back)
        {
            try
            {
                await Task.Run(() => _services.Switcher.SendCandidate(monitor, attempt.Protocol, back));
            }
            catch (DdcException ex)
            {
                _services.Log.Warn($"Wizard: switching back failed: {ex.Message}");
            }
        }

        if (!_wentSilent)
        {
            return;
        }

        // The monitor went quiet, so it really switched. Wait until it answers this computer again.
        status.Text = "Waiting for the screen to come back...";
        hint.Text = $"If the monitor still shows the other input, use its buttons to switch back to {_local.Name}. The wizard continues when it sees this computer again.";
        while (!_closeAfterTest)
        {
            if (await Task.Run(() => _services.Switcher.IsAnswering(monitor)) == true)
            {
                _cameBackQuickly = DateTime.UtcNow - revertedAt < QuickReturn;
                _services.Log.Info($"Wizard: monitor answered again after {(DateTime.UtcNow - revertedAt).TotalSeconds:0.0} s.");
                return;
            }
            await Task.Delay(700);
        }
    }

    private void ShowAttemptError(DdcException ex)
    {
        SetPage("Something went wrong", Ui.Text(ex.UserMessage));
        SetButtons(
            Ui.Button("Back", (_, _) => ShowInputsPage(_monitor!), primary: true),
            Ui.Button("Close", (_, _) => Close()));
    }

    private void ShowQuestionPage()
    {
        var target = _target!.Name;
        var switched = new RadioButton { Text = $"It switched to {target}: the other device appeared, or the screen stayed black or showed \"No signal\" for a few seconds", AutoSize = true, MaximumSize = Ui.Sz(Ui.ContentWidth, 0), Margin = Ui.Pad(0, 4, 0, 4), Checked = _wentSilent };
        var blinked = new RadioButton { Text = "It only blinked (black for about a second), then this computer's picture came straight back", AutoSize = true, MaximumSize = Ui.Sz(Ui.ContentWidth, 0), Margin = Ui.Pad(0, 4, 0, 4) };
        var nothing = new RadioButton { Text = "Nothing happened", AutoSize = true, Margin = Ui.Pad(0, 4, 0, 4) };
        var group1 = Ui.Stack();
        group1.Controls.AddRange([switched, blinked, nothing]);

        var byItself = new RadioButton { Text = "By itself", AutoSize = true, Margin = Ui.Pad(0, 4, 0, 4) };
        var buttons = new RadioButton { Text = "I used the monitor's buttons", AutoSize = true, Margin = Ui.Pad(0, 4, 0, 4) };
        var group2 = Ui.Stack();
        group2.Controls.Add(Ui.SectionHeader(Font, "How did the screen come back to this computer?"));
        group2.Controls.AddRange([byItself, buttons]);
        bool askHow = _cameBackQuickly != true;
        group2.Visible = askHow && switched.Checked;
        switched.CheckedChanged += (_, _) => group2.Visible = askHow && switched.Checked;

        var controls = new List<Control> { Ui.Text("What did the screen do?"), group1, group2 };
        if (_wentSilent)
        {
            controls.Insert(1, Ui.Text("The monitor stopped answering this computer during the test, which usually means it switched.", muted: true));
        }
        var status = Ui.Text("");
        status.ForeColor = Ui.Error;
        controls.Add(status);
        SetPage("Did it switch?", controls.ToArray());

        SetButtons(
            Ui.Button("Next", async (_, _) =>
            {
                if (switched.Checked)
                {
                    if (askHow && !byItself.Checked && !buttons.Checked)
                    {
                        status.Text = "Choose how the screen came back.";
                        return;
                    }
                    Accept(byItself.Checked, buttons.Checked);
                    ShowDonePage();
                }
                else if (blinked.Checked || nothing.Checked)
                {
                    _attemptIndex++;
                    if (_attemptIndex < _attempts.Count)
                    {
                        await RunAttemptAsync();
                    }
                    else
                    {
                        ShowFailurePage();
                    }
                }
                else
                {
                    status.Text = "Choose what happened.";
                }
            }, primary: true),
            Ui.Button("Cancel", (_, _) => Close()));
    }

    /// <summary>Turns the evidence and answers into the result.</summary>
    private void Accept(bool cameBackByItself, bool usedButtons)
    {
        _result = _attempts[_attemptIndex];
        if (_cameBackQuickly == true)
        {
            _ignoresCommandsWhenInactive = false; // this computer's switch-back worked
            _returnVerified = true;
        }
        else if (usedButtons)
        {
            _ignoresCommandsWhenInactive = true;
            _returnVerified = false;
        }
        else if (cameBackByItself && _cameBackQuickly is null)
        {
            _ignoresCommandsWhenInactive = false; // no read support to tell otherwise; trust the answer
            _returnVerified = true;
        }
        else
        {
            _ignoresCommandsWhenInactive = null; // came back late by itself: probably the monitor's no-signal fallback
            _returnVerified = false;
        }
        _services.Log.Info($"Wizard: {_target!.Name} works with {_result.Protocol.Id} {Hex.Format(_result.Target.Code)}; ignoresCommandsWhenInactive={_ignoresCommandsWhenInactive?.ToString() ?? "unknown"}.");
    }

    private void ShowDonePage()
    {
        var monitor = _monitor!;
        var result = _result!;
        var targetName = string.IsNullOrEmpty(_targetLabel) ? _target!.Name : $"{_targetLabel} ({_target!.Name})";

        var confirmed = Confirmed();
        bool needsProfile = SetupPlanner.NeedsUserProfile(monitor, result.Protocol, confirmed, _ignoresCommandsWhenInactive);
        var profile = BuildProfile(confirmed);

        var headline = new Label { Text = $"✓  {_target.Name} works", AutoSize = true, Font = Ui.Title(Font), ForeColor = Ui.Success, Margin = Ui.Pad(0, 4, 0, 8) };
        var controls = new List<Control>
        {
            headline,
            Ui.Text($"Code {Hex.Format(result.Target.Code)} with {result.Protocol.DisplayName}.", muted: true),
        };

        if (_ignoresCommandsWhenInactive == true || (_ignoresCommandsWhenInactive is null && monitor.Profile.EffectiveQuirks.IgnoresCommandsWhenInactive))
        {
            controls.Add(Ui.Text("This monitor only obeys the computer it is showing, so this computer can switch it away but not back. Install Monitor Switcher on the other computer to switch back, or use the monitor's buttons."));
        }

        var hotkeyBox = new HotkeyBox();
        var existing = _settings.Hotkeys.FirstOrDefault(h => h.MonitorId == monitor.StableId && h.Action == HotkeyAction.SwitchInput && h.InputId == _target.InputId);
        if (existing is not null && Hotkey.TryParse(existing.Keys, out var hk))
        {
            hotkeyBox.Hotkey = hk;
        }
        var hotkeyRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Ui.Pad(0, 10, 0, 0) };
        hotkeyRow.Controls.Add(new Label { Text = $"Shortcut for {targetName}", AutoSize = true, Margin = Ui.Pad(0, 6, 8, 0) });
        hotkeyRow.Controls.Add(hotkeyBox);
        controls.Add(hotkeyRow);

        controls.Add(Ui.Text(needsProfile
            ? $"Saving creates a profile for this monitor in {_services.Paths.UserProfilesDirectory}."
            : "These codes match the built-in profile, so only your settings change.", muted: true));

        var share = Ui.Button("Copy profile to share", (_, _) =>
        {
            Clipboard.SetText(ProfileCatalog.Serialize(profile));
            MessageBox.Show(this,
                "The profile is on the clipboard. Share it at github.com/ashrash/monitor-control (see \"Adding a monitor profile\") so it can ship built in.",
                "Monitor Switcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        share.Margin = Ui.Pad(0, 10, 0, 0);
        controls.Add(share);

        SetPage("All set", controls.ToArray());
        SetButtons(
            Ui.Button("Save", async (_, _) => await SaveAsync(needsProfile, profile, hotkeyBox.Hotkey), primary: true),
            Ui.Button("Cancel", (_, _) => Close()));
    }

    private void ShowFailurePage()
    {
        SetPage($"No code switched to {_target!.Name}",
            Ui.Text($"The wizard tried {_attempts.Count} code(s) and none of them switched the monitor."),
            Ui.SectionHeader(Font, "Things to check"),
            Ui.Text("• DDC/CI is turned on in the monitor's on-screen menu."),
            Ui.Text("• The cable or adapter passes DDC/CI. Some USB-C docks, KVMs and HDMI adapters don't; try a direct cable."),
            Ui.Text("• The device on the other input is switched on."),
            Ui.Text("• Run 'monitorswitcher --diagnose' and open an issue at github.com/ashrash/monitor-control with its output, so support for this monitor can be added.", muted: true));
        SetButtons(
            Ui.Button("Try another input", (_, _) => ShowInputsPage(_monitor!), primary: true),
            Ui.Button("Close", (_, _) => Close()));
    }

    // ---- Saving ---------------------------------------------------------------------------

    private List<ConfirmedInput> Confirmed()
    {
        var result = _result!;
        var list = new List<ConfirmedInput> { new(result.Target.InputId, _target!.Name, result.Target.Code, Verified: true) };
        if (result.Return is { } back)
        {
            list.Add(new ConfirmedInput(back.InputId, _local!.Name, back.Code, _returnVerified));
        }
        return list;
    }

    private MonitorProfile BuildProfile(List<ConfirmedInput> confirmed)
    {
        var monitor = _monitor!;
        var existingUser = _services.Profiles.Profiles.FirstOrDefault(p =>
            p.Origin == ProfileOrigin.User && ProfileMatcher.Evaluate(p, monitor.Identity, monitor.Capabilities) == MatchQuality.ProductCode);
        return SetupPlanner.BuildUserProfile(monitor, _result!.Protocol, confirmed, _ignoresCommandsWhenInactive, existingUser);
    }

    private async Task SaveAsync(bool needsProfile, MonitorProfile profile, Hotkey? hotkey)
    {
        var monitor = _monitor!;
        try
        {
            if (needsProfile)
            {
                Directory.CreateDirectory(_services.Paths.UserProfilesDirectory);
                var path = Path.Combine(_services.Paths.UserProfilesDirectory, profile.Id + ".json");
                File.WriteAllText(path, ProfileCatalog.Serialize(profile));
                _services.Log.Info($"Wizard saved user profile {path}.");
                _services.ReloadProfiles();
            }

            _services.Settings.Update(s =>
            {
                var ms = s.GetOrAddMonitor(monitor.StableId);
                ms.LocalInputId = _local!.InputId;
                ms.ToggleInputs = [_local.InputId, _target!.InputId];
                if (_targetLabel.Length > 0)
                {
                    ms.InputLabels[_target.InputId] = _targetLabel;
                }
                if (hotkey is { } hk)
                {
                    var keys = hk.ToString();
                    s.Hotkeys.RemoveAll(h => string.Equals(h.Keys, keys, StringComparison.OrdinalIgnoreCase)
                        || (h.MonitorId == monitor.StableId && h.Action == HotkeyAction.SwitchInput && h.InputId == _target.InputId));
                    s.Hotkeys.Add(new HotkeyBinding { MonitorId = monitor.StableId, Action = HotkeyAction.SwitchInput, InputId = _target.InputId, Keys = keys });
                }
            });

            await _onSaved();
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Could not save: {ex.Message}", "Monitor Switcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ---- Helpers --------------------------------------------------------------------------

    private string TransportSummary()
    {
        var reports = _services.Switcher.ProbeTransports();
        return "Interfaces: " + string.Join("; ", reports.Select(r => $"{r.Transport.DisplayName} {(r.Status.IsAvailable ? "✓" : "- " + r.Status.Summary)}"));
    }

    private static void AddRow(TableLayoutPanel grid, string label, Control control)
    {
        int row = grid.RowCount++;
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = Ui.Pad(0, 6, 16, 6) }, 0, row);
        control.Margin = Ui.Pad(0, 3, 0, 3);
        grid.Controls.Add(control, 1, row);
    }

    private void SetPage(string title, params Control[] controls)
    {
        _title.Text = title;
        _page.SuspendLayout();
        foreach (Control c in _page.Controls.Cast<Control>().ToList())
        {
            c.Dispose();
        }
        _page.Controls.Clear();
        foreach (var control in controls)
        {
            _page.Controls.Add(control);
        }
        _page.ResumeLayout();
    }

    private void SetButtons(params Button[] buttons)
    {
        foreach (Control c in _buttons.Controls.Cast<Control>().ToList())
        {
            c.Dispose();
        }
        _buttons.Controls.Clear();
        foreach (var button in buttons)
        {
            _buttons.Controls.Add(button);
        }
        AcceptButton = buttons.FirstOrDefault();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_testing)
        {
            // Never leave the monitor on a test input: stop, switch back, then close.
            e.Cancel = true;
            _closeAfterTest = true;
            _testCancel?.Cancel();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _testCancel?.Dispose();
        }
        base.Dispose(disposing);
    }
}
