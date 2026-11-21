using MonitorSwitcher.Core.Profiles;
using MonitorSwitcher.Core.Protocols;
using MonitorSwitcher.Core.Switching;

namespace MonitorSwitcher.Core.Setup;

public sealed record PortChoice(string InputId, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One thing the wizard will try: send <see cref="Target"/>, then switch back with <see cref="Return"/>.</summary>
public sealed record SetupAttempt(InputProtocol Protocol, InputCandidate Target, InputCandidate? Return);

/// <summary>An input the wizard tested, ready to be written into a profile.</summary>
public sealed record ConfirmedInput(string InputId, string Name, ushort Code, bool Verified);

/// <summary>
/// Decides what the setup wizard tries and in which order. Pure logic, so it is unit tested and
/// shared in spirit with the macOS app. Codes only ever come from the monitor's profile or from a
/// protocol's own candidate list.
/// </summary>
public static class SetupPlanner
{
    private static readonly string[] CanonicalOrder = ["hdmi1", "hdmi2", "hdmi3", "dp1", "dp2", "usbc", "usbc2", "dvi1", "dvi2", "vga1"];

    /// <summary>Ports to offer in the wizard's "which input" lists.</summary>
    public static IReadOnlyList<PortChoice> PortChoices(ResolvedMonitor monitor)
    {
        if (!monitor.NeedsSetup)
        {
            return monitor.Profile.Inputs.Select(i => new PortChoice(i.Id, i.Name)).ToList();
        }

        return UsableProtocols(monitor)
            .SelectMany(p => p.CandidatesFor(monitor.Capabilities))
            .GroupBy(c => c.InputId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PortChoice(g.Key, g.First().Name))
            .OrderBy(c => Array.IndexOf(CanonicalOrder, c.InputId) is var i and >= 0 ? i : int.MaxValue)
            .ThenBy(c => c.InputId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The profile's protocol first (if it has one), then protocols ranked for this monitor, limited to ones a transport can send.</summary>
    public static IReadOnlyList<InputProtocol> UsableProtocols(ResolvedMonitor monitor)
    {
        var ordered = new List<InputProtocol>();
        if (!monitor.NeedsSetup)
        {
            ordered.Add(monitor.Protocol);
        }
        foreach (var protocol in ProtocolRegistry.RankedFor(monitor.Identity, monitor.Capabilities))
        {
            if (!ordered.Contains(protocol))
            {
                ordered.Add(protocol);
            }
        }
        return ordered.Where(p => monitor.EndpointFor(p) is not null).ToList();
    }

    /// <summary>Candidates for a protocol: the profile's own codes first (when it uses this protocol), then the protocol's list.</summary>
    public static IReadOnlyList<InputCandidate> CandidatesFor(ResolvedMonitor monitor, InputProtocol protocol)
    {
        var result = new List<InputCandidate>();
        if (!monitor.NeedsSetup && string.Equals(monitor.Profile.Protocol, protocol.Id, StringComparison.OrdinalIgnoreCase))
        {
            result.AddRange(monitor.Profile.Inputs.Select(i => new InputCandidate(i.Id, i.Name, i.CodeValue)));
        }
        foreach (var candidate in protocol.CandidatesFor(monitor.Capabilities))
        {
            if (!result.Any(c => string.Equals(c.InputId, candidate.InputId, StringComparison.OrdinalIgnoreCase) && c.Code == candidate.Code))
            {
                result.Add(candidate);
            }
        }
        return result;
    }

    /// <summary>Every attempt for switching from <paramref name="localInputId"/> to <paramref name="targetInputId"/>.</summary>
    public static IReadOnlyList<SetupAttempt> Plan(ResolvedMonitor monitor, string localInputId, string targetInputId)
    {
        var attempts = new List<SetupAttempt>();
        foreach (var protocol in UsableProtocols(monitor))
        {
            var candidates = CandidatesFor(monitor, protocol);
            var back = candidates.FirstOrDefault(c => string.Equals(c.InputId, localInputId, StringComparison.OrdinalIgnoreCase));
            foreach (var target in candidates.Where(c => string.Equals(c.InputId, targetInputId, StringComparison.OrdinalIgnoreCase)))
            {
                attempts.Add(new SetupAttempt(protocol, target, back));
            }
        }
        return attempts;
    }

    /// <summary>
    /// True when the results differ from the profile already in use, so a user profile is needed.
    /// Confirming codes a built-in profile already has only needs settings, not a new profile.
    /// </summary>
    public static bool NeedsUserProfile(ResolvedMonitor monitor, InputProtocol protocol, IEnumerable<ConfirmedInput> confirmed, bool? ignoresCommandsWhenInactive)
    {
        if (monitor.NeedsSetup || !string.Equals(monitor.Profile.Protocol, protocol.Id, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (ignoresCommandsWhenInactive is { } quirk && quirk != monitor.Profile.EffectiveQuirks.IgnoresCommandsWhenInactive)
        {
            return true;
        }
        return confirmed.Any(c => monitor.Profile.FindInput(c.InputId) is not { } existing || existing.CodeValue != c.Code);
    }

    /// <summary>
    /// Builds (or extends) the user's profile for this monitor. It matches on manufacturer and
    /// product code only, so it never claims other models.
    /// </summary>
    public static MonitorProfile BuildUserProfile(
        ResolvedMonitor monitor,
        InputProtocol protocol,
        IEnumerable<ConfirmedInput> confirmed,
        bool? ignoresCommandsWhenInactive,
        MonitorProfile? existingUserProfile)
    {
        var identity = monitor.Identity;
        var sameProtocolBase = existingUserProfile is not null && string.Equals(existingUserProfile.Protocol, protocol.Id, StringComparison.OrdinalIgnoreCase)
            ? existingUserProfile
            : !monitor.NeedsSetup && string.Equals(monitor.Profile.Protocol, protocol.Id, StringComparison.OrdinalIgnoreCase)
                ? monitor.Profile
                : null;

        var inputs = sameProtocolBase?.Inputs
            .Select(i => new ProfileInput { Id = i.Id, Name = i.Name, Code = i.Code, Verified = i.Verified })
            .ToList() ?? [];

        foreach (var c in confirmed)
        {
            var existing = inputs.FirstOrDefault(i => string.Equals(i.Id, c.InputId, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                inputs.Add(new ProfileInput { Id = c.InputId, Name = c.Name, Code = Hex.Format(c.Code), Verified = c.Verified });
            }
            else
            {
                existing.Code = Hex.Format(c.Code);
                existing.Verified = c.Verified || (existing.Verified && existing.CodeValue == c.Code);
            }
        }

        var baseQuirks = sameProtocolBase?.EffectiveQuirks ?? ProfileQuirks.Default;
        var quirks = new ProfileQuirks
        {
            Retries = baseQuirks.Retries,
            RetryDelayMs = baseQuirks.RetryDelayMs,
            PostSwitchDelayMs = baseQuirks.PostSwitchDelayMs,
            IgnoresCommandsWhenInactive = ignoresCommandsWhenInactive ?? baseQuirks.IgnoresCommandsWhenInactive,
        };

        return new MonitorProfile
        {
            Schema = "../profile.schema.json",
            Id = existingUserProfile?.Id ?? $"{identity.ManufacturerId.ToLowerInvariant()}-{identity.ProductCode:x4}",
            Name = monitor.NeedsSetup ? identity.ModelName ?? $"{identity.ManufacturerId} 0x{identity.ProductCode:X4}" : monitor.Profile.Name,
            Match = new ProfileMatch
            {
                Manufacturer = identity.ManufacturerId,
                ProductCodes = [$"0x{identity.ProductCode:X4}"],
            },
            Protocol = protocol.Id,
            Inputs = inputs,
            Quirks = quirks,
            Notes = $"Created by the Monitor Switcher setup wizard on {DateTime.Now:yyyy-MM-dd} for {identity}.",
            Origin = ProfileOrigin.User,
        };
    }
}
