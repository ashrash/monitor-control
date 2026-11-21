using MonitorSwitcher.Core.Logging;
using MonitorSwitcher.Core.Profiles;
using MonitorSwitcher.Core.Storage;
using MonitorSwitcher.Core.Switching;
using MonitorSwitcher.Core.Transports;

namespace MonitorSwitcher.Core;

/// <summary>Everything a front end (tray app, CLI, wizard) needs, wired up by the platform layer.</summary>
public sealed class AppServices : IDisposable
{
    public required AppPaths Paths { get; init; }

    public required ILog Log { get; init; }

    public required IReadOnlyList<DdcTransport> Transports { get; init; }

    /// <summary>The current profile catalog (it can be reloaded through <see cref="SwitchService.ReloadProfiles"/>).</summary>
    public ProfileCatalog Profiles => Switcher.Catalog;

    /// <summary>Reloads built-in and user profiles from disk.</summary>
    public void ReloadProfiles() => Switcher.ReloadProfiles(ProfileCatalog.Load(Paths.UserProfilesDirectory, Log));

    public required JsonFileStore<AppSettings> Settings { get; init; }

    public required JsonFileStore<AppState> State { get; init; }

    public required SwitchService Switcher { get; init; }

    public void Dispose()
    {
        foreach (var transport in Transports)
        {
            transport.Dispose();
        }
    }
}
