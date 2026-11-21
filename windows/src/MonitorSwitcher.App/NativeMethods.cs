using System.Runtime.InteropServices;

namespace MonitorSwitcher.App;

internal static partial class NativeMethods
{
    public const int WmHotkey = 0x0312;
    public const uint ModNoRepeat = 0x4000;
    public static readonly nint HwndBroadcast = 0xFFFF;

    /// <summary>Sent by a second launch of the app to ask the running instance to show itself.</summary>
    public static readonly uint ActivateMessage = RegisterWindowMessage("MonitorSwitcher.Activate.{7F3C2A61}");

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessage(string message);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hwnd, int id);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ChangeWindowMessageFilterEx(nint hwnd, uint message, uint action, nint changeFilterStruct);

    [LibraryImport("user32.dll")]
    private static partial short GetKeyState(int virtualKey);

    public static bool IsWinKeyDown() =>
        (GetKeyState(0x5B) & 0x8000) != 0 || (GetKeyState(0x5C) & 0x8000) != 0;
}
