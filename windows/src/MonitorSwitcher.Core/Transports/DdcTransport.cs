using MonitorSwitcher.Core.Ddc;
using MonitorSwitcher.Core.Monitors;
using MonitorSwitcher.Core.Protocols;
using MonitorSwitcher.Core.Storage;

namespace MonitorSwitcher.Core.Transports;

public sealed record TransportStatus(bool IsAvailable, string Summary, string? Hint = null)
{
    public static TransportStatus Available(string summary) => new(true, summary);

    public static TransportStatus Unavailable(string summary, string? hint = null) => new(false, summary, hint);
}

/// <summary>A monitor as seen through one transport.</summary>
public sealed class DisplayEndpoint(DdcTransport transport, MonitorIdentity identity, string connectorId, object? native)
{
    public DdcTransport Transport { get; } = transport;

    public MonitorIdentity Identity { get; } = identity;

    /// <summary>Transport-specific location, e.g. "gpu0/output 0x100".</summary>
    public string ConnectorId { get; } = connectorId;

    /// <summary>OS display name when known, e.g. \\.\DISPLAY1 on Windows.</summary>
    public string? OsDisplayName { get; set; }

    /// <summary>True when the OS no longer lists this monitor and it was rebuilt from the last known address.</summary>
    public bool IsRestoredFromCache { get; init; }

    /// <summary>Transport-private handle data.</summary>
    public object? Native { get; } = native;

    public override string ToString() => $"{Identity} via {Transport.Id} {ConnectorId}";
}

public readonly record struct VcpReading(ushort Current, ushort Maximum);

/// <summary>
/// Layer 1: moves DDC/CI bytes to a monitor (NVIDIA NvAPI, AMD ADL, Intel IGCL, Windows dxva2,
/// macOS IOAVService). Knows nothing about which bytes switch inputs.
/// </summary>
/// <remarks>
/// <see cref="Send"/> is deliberately not virtual: it runs the safety guard and then calls
/// <see cref="SendCore"/>, so no transport can skip the checks. Implementations must also call
/// <see cref="DdcGuard.EnsureDeviceAddress"/> right before their native write, and must never
/// accept a device address from outside: it is always <see cref="DdcConstants.DisplayDeviceAddress"/>.
/// See docs/adding-a-transport.md.
/// </remarks>
public abstract class DdcTransport : IDisposable
{
    /// <summary>Short id, e.g. "nvapi".</summary>
    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    /// <summary>True if the transport can write arbitrary DDC/CI frames (any source byte).</summary>
    public abstract bool SupportsRawI2C { get; }

    /// <summary>Lower is preferred when several transports reach the same monitor.</summary>
    public abstract int Priority { get; }

    /// <summary>Is the driver API present and usable? Must not throw.</summary>
    public abstract TransportStatus Probe();

    /// <summary>Monitors this transport can reach right now.</summary>
    public abstract IReadOnlyList<DisplayEndpoint> Enumerate();

    public bool CanSend(InputProtocol protocol) => SupportsRawI2C || !protocol.RequiresRawI2C;

    public bool CanSend(in SetVcpFrame frame) =>
        SupportsRawI2C || frame.SourceAddress == DdcConstants.StandardSourceAddress;

    public void Send(DisplayEndpoint endpoint, SetVcpFrame frame)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!ReferenceEquals(endpoint.Transport, this))
        {
            throw new ArgumentException("The endpoint belongs to a different transport.", nameof(endpoint));
        }

        DdcGuard.EnsureFrame(frame);
        if (!CanSend(frame))
        {
            throw new DdcException(
                DdcErrorKind.UnsupportedByTransport,
                $"{DisplayName} can only send standard MCCS commands, not {frame}.");
        }

        SendCore(endpoint, frame);
    }

    /// <summary>Delivers an already validated frame.</summary>
    protected abstract void SendCore(DisplayEndpoint endpoint, SetVcpFrame frame);

    /// <summary>True if <see cref="TryReadVcp"/> is implemented, so a null result means "no answer".</summary>
    public virtual bool SupportsReads => false;

    /// <summary>Reads the DDC/CI capabilities string, or null if unsupported or unreadable.</summary>
    public virtual string? TryReadCapabilities(DisplayEndpoint endpoint) => null;

    /// <summary>Reads a VCP value, or null if unsupported or unreadable.</summary>
    public virtual VcpReading? TryReadVcp(DisplayEndpoint endpoint, byte vcpCode) => null;

    /// <summary>A record that lets <see cref="TryRestore"/> find the endpoint again later.</summary>
    public virtual EndpointRecord? Describe(DisplayEndpoint endpoint) => null;

    /// <summary>Rebuilds an endpoint the OS no longer lists (for example while the monitor shows another input).</summary>
    public virtual DisplayEndpoint? TryRestore(EndpointRecord record, MonitorIdentity identity) => null;

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
