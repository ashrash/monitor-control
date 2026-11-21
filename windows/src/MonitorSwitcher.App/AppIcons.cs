namespace MonitorSwitcher.App;

internal static class AppIcons
{
    /// <summary>The app icon at the requested size (picks the best frame from the embedded .ico).</summary>
    public static Icon Get(Size size)
    {
        using var stream = typeof(AppIcons).Assembly.GetManifestResourceStream("app.ico")!;
        return new Icon(stream, size);
    }

    public static Icon Tray() => Get(SystemInformation.SmallIconSize);

    public static Icon Window() => Get(SystemInformation.IconSize);
}

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MonitorSwitcher";

    public static bool IsEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string command
            && command.Contains(Environment.ProcessPath ?? "", StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
