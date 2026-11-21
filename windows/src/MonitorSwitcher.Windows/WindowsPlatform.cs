using MonitorSwitcher.Core;
using MonitorSwitcher.Core.Logging;
using MonitorSwitcher.Core.Profiles;
using MonitorSwitcher.Core.Storage;
using MonitorSwitcher.Core.Switching;
using MonitorSwitcher.Core.Transports;
using MonitorSwitcher.Windows.Displays;
using MonitorSwitcher.Windows.Transports.Adl;
using MonitorSwitcher.Windows.Transports.Igcl;
using MonitorSwitcher.Windows.Transports.Dxva2Api;
using MonitorSwitcher.Windows.Transports.NvApi;

namespace MonitorSwitcher.Windows;

/// <summary>Composition root for Windows: builds transports, stores and the switch service.</summary>
public static class WindowsPlatform
{
    /// <summary>%LOCALAPPDATA%\MonitorSwitcher, or MONITORSWITCHER_HOME if set (handy for testing).</summary>
    public static AppPaths DefaultPaths()
    {
        var overridden = Environment.GetEnvironmentVariable("MONITORSWITCHER_HOME");
        return new AppPaths(!string.IsNullOrWhiteSpace(overridden)
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorSwitcher"));
    }

    /// <param name="processTag">Short tag written on each log line, e.g. "tray" or "cli".</param>
    /// <param name="extraLog">Optional second sink, e.g. the console for <c>--verbose</c>.</param>
    public static AppServices Create(string processTag, ILog? extraLog = null, LogLevel fileLogLevel = LogLevel.Info)
    {
        var paths = DefaultPaths();
        var fileLog = new RollingFileLog(paths.LogsDirectory, processTag, fileLogLevel);
        ILog log = extraLog is null ? fileLog : new TeeLog(fileLog, extraLog);

        var displays = new WindowsDisplayEnumerator(log);
        DdcTransport[] transports =
        [
            new NvApiTransport(displays, log),
            new AdlTransport(log),
            new IgclTransport(displays, log),
            new Dxva2Transport(displays, log),
        ];

        var settings = new JsonFileStore<AppSettings>(
            paths.SettingsFile, CoreJsonContext.Files.AppSettings, new NamedMutexLock(NamedMutexLock.SettingsFile), log);
        var state = new JsonFileStore<AppState>(
            paths.StateFile, CoreJsonContext.Files.AppState, new NamedMutexLock(NamedMutexLock.StateFile), log);
        var catalog = ProfileCatalog.Load(paths.UserProfilesDirectory, log);
        var switcher = new SwitchService(transports, catalog, state, new NamedMutexLock(NamedMutexLock.DdcBus), log);

        return new AppServices
        {
            Paths = paths,
            Log = log,
            Transports = transports,
            Settings = settings,
            State = state,
            Switcher = switcher,
        };
    }
}
