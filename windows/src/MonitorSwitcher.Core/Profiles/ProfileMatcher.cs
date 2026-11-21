using MonitorSwitcher.Core.Monitors;

namespace MonitorSwitcher.Core.Profiles;

/// <summary>How specifically a profile matched a monitor. Higher is better.</summary>
public enum MatchQuality
{
    None = 0,
    /// <summary>No profile matched; a standard-MCCS profile was generated.</summary>
    Fallback = 1,
    ModelName = 2,
    CapabilitiesModel = 3,
    ProductCode = 4,
}

public sealed record ProfileMatchResult(MonitorProfile Profile, MatchQuality Quality);

/// <summary>Picks the best profile for a monitor. Pure function: easy to unit test.</summary>
public static class ProfileMatcher
{
    public static MatchQuality Evaluate(MonitorProfile profile, MonitorIdentity monitor, MonitorCapabilities? capabilities)
    {
        var match = profile.Match;
        if (!string.Equals(match.Manufacturer, monitor.ManufacturerId, StringComparison.OrdinalIgnoreCase))
        {
            return MatchQuality.None;
        }

        if (match.ProductCodes?.Any(c => Hex.TryParseUInt16(c, out var code) && code == monitor.ProductCode) == true)
        {
            return MatchQuality.ProductCode;
        }

        if (!string.IsNullOrWhiteSpace(match.CapabilitiesModel)
            && capabilities?.Model is { } capsModel
            && string.Equals(match.CapabilitiesModel.Trim(), capsModel.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return MatchQuality.CapabilitiesModel;
        }

        if (monitor.ModelName is { } name
            && match.ModelNames?.Any(n => string.Equals(n.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)) == true)
        {
            return MatchQuality.ModelName;
        }

        return MatchQuality.None;
    }

    /// <summary>
    /// Best match, or null. Ties go to user profiles over built-ins (so users can override a
    /// shipped profile), then to the alphabetically first id for determinism.
    /// </summary>
    public static ProfileMatchResult? FindBest(
        IEnumerable<MonitorProfile> profiles,
        MonitorIdentity monitor,
        MonitorCapabilities? capabilities)
    {
        return profiles
            .Select(p => new ProfileMatchResult(p, Evaluate(p, monitor, capabilities)))
            .Where(r => r.Quality > MatchQuality.Fallback)
            .OrderByDescending(r => r.Quality)
            .ThenBy(r => r.Profile.Origin == ProfileOrigin.User ? 0 : 1)
            .ThenBy(r => r.Profile.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
