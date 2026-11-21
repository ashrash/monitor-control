using System.Reflection;
using MonitorSwitcher.Core.Ddc;
using MonitorSwitcher.Core.Logging;
using MonitorSwitcher.Core.Switching;
using MonitorSwitcher.Windows;

namespace MonitorSwitcher.App;

internal static class Program
{
    private const string InstanceMutexName = @"Local\MonitorSwitcher.Tray";

    public static string Version =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    [STAThread]
    private static int Main(string[] args)
    {
        // Desktop shortcuts and launchers can run "MonitorSwitcher.exe --input usbc" without a console.
        if (args.Length > 0 && args[0] is "--input" or "-i" or "--toggle" or "-t")
        {
            return RunOneShot(args);
        }

        bool openWizard = args.Contains("--setup", StringComparer.OrdinalIgnoreCase);
        using var instance = new Mutex(initiallyOwned: true, InstanceMutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            // Already running: ask that instance to open Settings (or the wizard), then exit.
            NativeMethods.PostMessage(NativeMethods.HwndBroadcast, NativeMethods.ActivateMessage, openWizard ? 1 : 0, 0);
            return 0;
        }

        ApplicationConfiguration.Initialize();
#pragma warning disable WFO5001 // dark mode support is marked experimental
        Application.SetColorMode(SystemColorMode.System);
#pragma warning restore WFO5001

        using var services = WindowsPlatform.Create("tray");
        services.Log.Info($"Monitor Switcher {Version} tray app starting.");
        bool firstRun = !File.Exists(services.Paths.SettingsFile);
        using var context = new TrayContext(services, firstRun, openWizard);
        Application.Run(context);
        services.Log.Info("Tray app exiting.");
        return 0;
    }

    private static int RunOneShot(string[] args)
    {
        string? input = null, monitor = null;
        bool toggle = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--input" or "-i" when i + 1 < args.Length: input = args[++i]; break;
                case "--monitor" or "-m" when i + 1 < args.Length: monitor = args[++i]; break;
                case "--toggle" or "-t": toggle = true; break;
            }
        }

        using var services = WindowsPlatform.Create("shortcut");
        try
        {
            var settings = services.Settings.Load();
            var target = MonitorResolver.Resolve(services.Switcher.DiscoverMonitors(settings), settings, monitor);
            var monitorSettings = settings.FindMonitor(target.StableId);
            if (toggle)
            {
                services.Switcher.Toggle(target, monitorSettings);
            }
            else if (input is not null)
            {
                services.Switcher.SwitchTo(target, InputResolver.Resolve(target.Profile, monitorSettings, input).Id, monitorSettings);
            }
            return 0;
        }
        catch (DdcException ex)
        {
            services.Log.Error($"Shortcut: {ex.Message}");
            MessageBox.Show(ex.UserMessage, "Monitor Switcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }
}
