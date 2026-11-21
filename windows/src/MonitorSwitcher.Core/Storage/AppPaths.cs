namespace MonitorSwitcher.Core.Storage;

/// <summary>Where the app keeps its files. The platform layer chooses the root folder.</summary>
public sealed class AppPaths(string root)
{
    public string Root { get; } = root;

    public string SettingsFile => System.IO.Path.Combine(Root, "settings.json");

    public string StateFile => System.IO.Path.Combine(Root, "state.json");

    /// <summary>User profiles. Same format as the built-in ones; a user profile wins a tie.</summary>
    public string UserProfilesDirectory => System.IO.Path.Combine(Root, "profiles");

    public string LogsDirectory => System.IO.Path.Combine(Root, "logs");
}
