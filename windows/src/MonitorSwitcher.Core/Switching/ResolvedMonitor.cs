using MonitorSwitcher.Core.Monitors;
using MonitorSwitcher.Core.Profiles;
using MonitorSwitcher.Core.Protocols;
using MonitorSwitcher.Core.Transports;

namespace MonitorSwitcher.Core.Switching;

/// <summary>A connected monitor with its profile and every transport that can reach it.</summary>
public sealed class ResolvedMonitor
{
    public required MonitorIdentity Identity { get; init; }

    /// <summary>Endpoints ordered by transport preference.</summary>
    public required IReadOnlyList<DisplayEndpoint> Endpoints { get; init; }

    public required MonitorProfile Profile { get; init; }

    public required MatchQuality Quality { get; init; }

    public MonitorCapabilities? Capabilities { get; init; }

    public string StableId => Identity.StableId;

    public string Name => Profile.Origin == ProfileOrigin.Fallback
        ? Identity.ModelName ?? Profile.Name
        : Profile.Name;

    public string? OsDisplayName => Endpoints.Select(e => e.OsDisplayName).FirstOrDefault(n => n is not null);

    /// <summary>True when the OS no longer lists the monitor (for example while it shows another computer).</summary>
    public bool IsRestoredFromCache => Endpoints.All(e => e.IsRestoredFromCache);

    /// <summary>True when no profile matched and the setup wizard should be offered.</summary>
    public bool NeedsSetup => Profile.Origin == ProfileOrigin.Fallback;

    public InputProtocol Protocol => ProtocolRegistry.Get(Profile.Protocol);

    /// <summary>The preferred endpoint whose transport can deliver <paramref name="protocol"/>'s frames.</summary>
    public DisplayEndpoint? EndpointFor(InputProtocol protocol) =>
        Endpoints.FirstOrDefault(e => e.Transport.CanSend(protocol));

    public override string ToString() => $"{Name} ({StableId})";
}

public sealed record SwitchOutcome(
    ResolvedMonitor Monitor,
    string InputId,
    string InputName,
    ushort Code,
    DisplayEndpoint Endpoint,
    int Attempts,
    TimeSpan Elapsed);
