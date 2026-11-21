using System.Text;

namespace MonitorSwitcher.App;

[Flags]
internal enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>A global key combination such as Ctrl+Alt+1, stored in settings as text.</summary>
internal readonly record struct Hotkey(HotkeyModifiers Modifiers, Keys Key)
{
    /// <summary>
    /// Needs Ctrl, Alt or Win (Shift alone would steal normal typing), except for F13-F24 style
    /// keys that nothing types.
    /// </summary>
    public bool IsValid =>
        Key != Keys.None
        && !IsModifierKey(Key)
        && ((Modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0
            || Key is >= Keys.F13 and <= Keys.F24);

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        Keys key = Keys.None;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= HotkeyModifiers.Control; break;
                case "alt": modifiers |= HotkeyModifiers.Alt; break;
                case "shift": modifiers |= HotkeyModifiers.Shift; break;
                case "win" or "windows": modifiers |= HotkeyModifiers.Win; break;
                default:
                    if (key != Keys.None || !TryParseKey(raw, out key))
                    {
                        return false;
                    }
                    break;
            }
        }

        hotkey = new Hotkey(modifiers, key);
        return hotkey.IsValid;
    }

    public static Hotkey FromKeyEvent(KeyEventArgs e)
    {
        var modifiers = HotkeyModifiers.None;
        if (e.Control)
        {
            modifiers |= HotkeyModifiers.Control;
        }
        if (e.Alt)
        {
            modifiers |= HotkeyModifiers.Alt;
        }
        if (e.Shift)
        {
            modifiers |= HotkeyModifiers.Shift;
        }
        if (NativeMethods.IsWinKeyDown())
        {
            modifiers |= HotkeyModifiers.Win;
        }
        return new Hotkey(modifiers, e.KeyCode);
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            sb.Append("Ctrl+");
        }
        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            sb.Append("Alt+");
        }
        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            sb.Append("Shift+");
        }
        if (Modifiers.HasFlag(HotkeyModifiers.Win))
        {
            sb.Append("Win+");
        }
        return sb.Append(KeyName(Key)).ToString();
    }

    public static bool IsModifierKey(Keys key) => key is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
        or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or Keys.Menu or Keys.LMenu or Keys.RMenu
        or Keys.LWin or Keys.RWin;

    private static string KeyName(Keys key) => key switch
    {
        >= Keys.D0 and <= Keys.D9 => ((char)('0' + (key - Keys.D0))).ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => $"Num{key - Keys.NumPad0}",
        _ => FriendlyNames.FirstOrDefault(n => n.Key == key).Name ?? key.ToString(),
    };

    /// <summary>Readable names for punctuation keys (symbols like "+" would clash with the separator).</summary>
    private static readonly (Keys Key, string Name)[] FriendlyNames =
    [
        (Keys.Oemcomma, "Comma"),
        (Keys.OemPeriod, "Period"),
        (Keys.OemMinus, "Minus"),
        (Keys.Oemplus, "Plus"),
        (Keys.OemQuotes, "Quote"),
        (Keys.OemSemicolon, "Semicolon"),
        (Keys.OemOpenBrackets, "LeftBracket"),
        (Keys.OemCloseBrackets, "RightBracket"),
        (Keys.OemPipe, "Backslash"),
        (Keys.OemQuestion, "Slash"),
        (Keys.Oemtilde, "Backtick"),
        (Keys.Next, "PageDown"),
        (Keys.Prior, "PageUp"),
    ];

    private static bool TryParseKey(string token, out Keys key)
    {
        key = Keys.None;
        if (token.Length == 1 && char.IsDigit(token[0]))
        {
            key = Keys.D0 + (token[0] - '0');
            return true;
        }
        if (token.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && token.Length == 4 && char.IsDigit(token[3]))
        {
            key = Keys.NumPad0 + (token[3] - '0');
            return true;
        }
        var friendly = FriendlyNames.FirstOrDefault(n => string.Equals(n.Name, token, StringComparison.OrdinalIgnoreCase));
        key = friendly.Name is not null
            ? friendly.Key
            : Enum.TryParse<Keys>(token, ignoreCase: true, out var parsed) ? parsed : Keys.None;
        return key != Keys.None;
    }
}

/// <summary>
/// Hidden top-level window that receives WM_HOTKEY and the "activate" broadcast from a second
/// launch. (Message-only windows don't receive broadcasts, so this is a normal hidden window.)
/// </summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private readonly Action<int> _onHotkey;
    private readonly Action<bool> _onActivate;
    private readonly HashSet<int> _registered = [];

    /// <param name="onActivate">Called when a second launch asks this instance to show itself; true = open the setup wizard.</param>
    public HotkeyWindow(Action<int> onHotkey, Action<bool> onActivate)
    {
        _onHotkey = onHotkey;
        _onActivate = onActivate;
        CreateHandle(new CreateParams { Caption = "Monitor Switcher hotkeys" });
        NativeMethods.ChangeWindowMessageFilterEx(Handle, NativeMethods.ActivateMessage, 1 /* MSGFLT_ALLOW */, 0);
    }

    /// <returns>False if another application already owns the combination.</returns>
    public bool TryRegister(int id, Hotkey hotkey)
    {
        if (!NativeMethods.RegisterHotKey(Handle, id, (uint)hotkey.Modifiers | NativeMethods.ModNoRepeat, (uint)hotkey.Key))
        {
            return false;
        }
        _registered.Add(id);
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered)
        {
            NativeMethods.UnregisterHotKey(Handle, id);
        }
        _registered.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WmHotkey)
        {
            _onHotkey((int)m.WParam);
            return;
        }
        if (m.Msg == (int)NativeMethods.ActivateMessage)
        {
            _onActivate(m.WParam == 1);
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterAll();
        DestroyHandle();
    }
}
