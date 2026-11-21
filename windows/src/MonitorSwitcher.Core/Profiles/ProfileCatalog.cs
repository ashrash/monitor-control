using System.Text.Json;
using MonitorSwitcher.Core.Logging;
using MonitorSwitcher.Core.Monitors;
using MonitorSwitcher.Core.Protocols;
using MonitorSwitcher.Core.Storage;

namespace MonitorSwitcher.Core.Profiles;

public sealed record ProfileLoadError(string Source, string Message);

public sealed class ProfileFormatException(string source, IReadOnlyList<string> errors)
    : Exception($"{source}: {string.Join(" ", errors)}")
{
    public string ProfileSource { get; } = source;

    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Built-in profiles (embedded) plus the user's profiles folder.</summary>
public sealed class ProfileCatalog
{
    private const string ResourcePrefix = "MonitorSwitcher.Profiles.";

    public ProfileCatalog(IReadOnlyList<MonitorProfile> profiles, IReadOnlyList<ProfileLoadError> errors)
    {
        Profiles = profiles;
        Errors = errors;
    }

    public IReadOnlyList<MonitorProfile> Profiles { get; }

    /// <summary>Profiles that failed to load. Shown by <c>--diagnose</c>; never fatal.</summary>
    public IReadOnlyList<ProfileLoadError> Errors { get; }

    public static ProfileCatalog Load(string? userProfilesDirectory, ILog log)
    {
        var errors = new List<ProfileLoadError>();
        var profiles = new List<MonitorProfile>(LoadBuiltIn(errors));

        if (userProfilesDirectory is not null && Directory.Exists(userProfilesDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(userProfilesDirectory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    profiles.Add(Parse(File.ReadAllText(file), ProfileOrigin.User, file));
                }
                catch (Exception ex) when (ex is ProfileFormatException or IOException or UnauthorizedAccessException)
                {
                    errors.Add(new ProfileLoadError(file, ex.Message));
                }
            }
        }

        foreach (var error in errors)
        {
            log.Warn($"Profile not loaded: {error.Source}: {error.Message}");
        }
        return new ProfileCatalog(profiles, errors);
    }

    public static IReadOnlyList<MonitorProfile> LoadBuiltIn(List<ProfileLoadError> errors)
    {
        var assembly = typeof(ProfileCatalog).Assembly;
        var result = new List<MonitorProfile>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            try
            {
                result.Add(Parse(reader.ReadToEnd(), ProfileOrigin.BuiltIn, name[ResourcePrefix.Length..]));
            }
            catch (ProfileFormatException ex)
            {
                errors.Add(new ProfileLoadError(name, ex.Message));
            }
        }
        return result;
    }

    /// <summary>Parses and validates one profile.</summary>
    /// <exception cref="ProfileFormatException">The JSON is malformed or fails validation.</exception>
    public static MonitorProfile Parse(string json, ProfileOrigin origin, string source)
    {
        MonitorProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize(json, CoreJsonContext.Default.MonitorProfile);
        }
        catch (JsonException ex)
        {
            throw new ProfileFormatException(source, [ex.Message]);
        }

        if (profile is null)
        {
            throw new ProfileFormatException(source, ["The file is empty."]);
        }

        var errors = ProfileValidator.Validate(profile);
        if (errors.Count > 0)
        {
            throw new ProfileFormatException(source, errors);
        }

        profile.Origin = origin;
        profile.SourcePath = source;
        return profile;
    }

    public static string Serialize(MonitorProfile profile) =>
        JsonSerializer.Serialize(profile, CoreJsonContext.Files.MonitorProfile);

    /// <summary>Finds a profile by id; a user profile wins over a built-in with the same id.</summary>
    public MonitorProfile? FindById(string id) =>
        Profiles
            .Where(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Origin == ProfileOrigin.User ? 0 : 1)
            .FirstOrDefault();

    /// <summary>
    /// The profile to use for a monitor: a forced profile id from settings, else the best match,
    /// else a generated standard-MCCS profile (which the UI flags as needing the setup wizard).
    /// </summary>
    public ProfileMatchResult Resolve(MonitorIdentity monitor, MonitorCapabilities? capabilities, string? forcedProfileId = null)
    {
        if (forcedProfileId is not null && FindById(forcedProfileId) is { } forced)
        {
            return new ProfileMatchResult(forced, MatchQuality.ProductCode);
        }

        return ProfileMatcher.FindBest(Profiles, monitor, capabilities)
            ?? new ProfileMatchResult(CreateFallback(monitor, capabilities), MatchQuality.Fallback);
    }

    /// <summary>Standard MCCS profile for an unknown monitor. Every input is unverified.</summary>
    public static MonitorProfile CreateFallback(MonitorIdentity monitor, MonitorCapabilities? capabilities)
    {
        var protocol = new StandardMccsProtocol();
        var inputs = protocol.CandidatesFor(capabilities)
            .GroupBy(c => c.InputId)
            .Select(g => g.First())
            .Select(c => new ProfileInput { Id = c.InputId, Name = c.Name, Code = Hex.Format(c.Code) })
            .ToList();

        return new MonitorProfile
        {
            Id = $"auto-{monitor.ManufacturerId.ToLowerInvariant()}-{monitor.ProductCode:x4}",
            Name = monitor.ModelName ?? $"{monitor.ManufacturerId} monitor 0x{monitor.ProductCode:X4}",
            Match = new ProfileMatch
            {
                Manufacturer = monitor.ManufacturerId,
                ProductCodes = [$"0x{monitor.ProductCode:X4}"],
            },
            Protocol = StandardMccsProtocol.ProtocolId,
            Inputs = inputs,
            Origin = ProfileOrigin.Fallback,
            Notes = "Generated automatically. Run the setup wizard to confirm which codes work.",
        };
    }
}
