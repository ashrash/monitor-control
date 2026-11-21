namespace MonitorSwitcher.App;

/// <summary>Shared look: fonts, colours and spacing for the code-built windows.</summary>
internal static class Ui
{
    public const int ContentWidth = 560;

    /// <summary>
    /// Device pixels per logical (96 DPI) pixel. The windows are built in code and pages are
    /// swapped at runtime, which WinForms' automatic scaling misses, so every pixel constant goes
    /// through <see cref="Px"/>, <see cref="Pad"/> or <see cref="Sz"/> instead.
    /// </summary>
    private static readonly float Scale = GetScale();

    public static int Px(int logical) => (int)Math.Round(logical * Scale);

    public static Padding Pad(int left, int top, int right, int bottom) => new(Px(left), Px(top), Px(right), Px(bottom));

    public static Size Sz(int width, int height) => new(Px(width), Px(height));

    private static float GetScale()
    {
        using var g = Graphics.FromHwnd(0);
        return g.DpiX / 96f;
    }

    public static Font Title(Font font) => new(font.FontFamily, font.Size + 5f, FontStyle.Regular);

    public static Font Section(Font font) => new(font.FontFamily, font.Size + 1.5f, FontStyle.Bold);

    public static Color Muted => SystemColors.GrayText;

    public static Color Error => Color.FromArgb(0xC4, 0x2B, 0x1C);

    public static Color Success => Color.FromArgb(0x10, 0x7C, 0x10);

    public static Label Text(string text, bool muted = false, int width = ContentWidth) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = Sz(width, 0),
        ForeColor = muted ? Muted : SystemColors.ControlText,
        Margin = Ui.Pad(0, 3, 0, 3),
    };

    public static Label SectionHeader(Font font, string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = Section(font),
        Margin = Ui.Pad(0, 16, 0, 6),
    };

    public static TableLayoutPanel Stack() => new()
    {
        ColumnCount = 1,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Margin = Padding.Empty,
        Padding = Padding.Empty,
    };

    public static Button Button(string text, EventHandler onClick, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = Sz(88, 30),
            Padding = Ui.Pad(10, 2, 10, 2),
            Margin = Ui.Pad(6, 0, 0, 0),
            UseVisualStyleBackColor = true,
        };
        if (primary)
        {
            button.Font = new Font(button.Font, FontStyle.Bold);
        }
        button.Click += onClick;
        return button;
    }

    public static void ApplyFormDefaults(Form form, string title)
    {
        form.Text = title;
        form.Icon = AppIcons.Window();
        form.AutoScaleMode = AutoScaleMode.None; // see Ui.Scale
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MaximizeBox = false;
        form.MinimizeBox = true;
        form.StartPosition = FormStartPosition.CenterScreen;
        form.AutoSize = true;
        form.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        form.ShowInTaskbar = true;
    }

    /// <summary>Root layout for a window; padding lives here because AutoSize forms ignore their own padding.</summary>
    public static TableLayoutPanel Root()
    {
        var root = Stack();
        root.Padding = Ui.Pad(24, 16, 24, 20);
        return root;
    }
}

/// <summary>
/// A read-only text box that records a key combination: click it and press e.g. Ctrl+Alt+2.
/// Backspace or Delete clears it.
/// </summary>
internal sealed class HotkeyBox : TextBox
{
    private Hotkey? _hotkey;

    public HotkeyBox()
    {
        ReadOnly = true;
        ShortcutsEnabled = false;
        Cursor = Cursors.Hand;
        PlaceholderText = "Click to set";
        Width = Ui.Px(150);
        BackColor = SystemColors.Window;
    }

    public event EventHandler? HotkeyChanged;

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Hotkey? Hotkey
    {
        get => _hotkey;
        set
        {
            _hotkey = value;
            Text = value?.ToString() ?? "";
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        var modifiers = keyData & Keys.Modifiers;

        if ((key == Keys.Tab && (modifiers is Keys.None or Keys.Shift)) || (key == Keys.Escape && modifiers == Keys.None) || (key == Keys.Enter && modifiers == Keys.None))
        {
            return base.ProcessCmdKey(ref msg, keyData);
        }

        if (key is Keys.Back or Keys.Delete && modifiers == Keys.None)
        {
            SetAndNotify(null);
            return true;
        }

        var candidate = App.Hotkey.FromKeyEvent(new KeyEventArgs(keyData));
        if (App.Hotkey.IsModifierKey(key))
        {
            Text = candidate.Modifiers == HotkeyModifiers.None ? Text : $"{FormatModifiers(candidate.Modifiers)}...";
            return true;
        }

        if (!candidate.IsValid)
        {
            Text = $"{candidate}  - add Ctrl, Alt or Win";
            return true;
        }

        SetAndNotify(candidate);
        return true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (App.Hotkey.IsModifierKey(e.KeyCode) && !Text.Equals(_hotkey?.ToString() ?? "", StringComparison.Ordinal) && Text.EndsWith("...", StringComparison.Ordinal))
        {
            Text = _hotkey?.ToString() ?? "";
        }
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        Text = _hotkey?.ToString() ?? "";
    }

    private void SetAndNotify(Hotkey? hotkey)
    {
        Hotkey = hotkey;
        HotkeyChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string FormatModifiers(HotkeyModifiers m) =>
        string.Join("+", new[]
        {
            m.HasFlag(HotkeyModifiers.Control) ? "Ctrl" : null,
            m.HasFlag(HotkeyModifiers.Alt) ? "Alt" : null,
            m.HasFlag(HotkeyModifiers.Shift) ? "Shift" : null,
            m.HasFlag(HotkeyModifiers.Win) ? "Win" : null,
        }.Where(s => s is not null));
}
