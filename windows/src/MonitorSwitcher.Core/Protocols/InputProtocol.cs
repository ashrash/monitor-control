using MonitorSwitcher.Core.Ddc;
using MonitorSwitcher.Core.Monitors;

namespace MonitorSwitcher.Core.Protocols;

/// <summary>A code worth trying for a port during the setup wizard.</summary>
/// <param name="InputId">Canonical port id: hdmi1, hdmi2, dp1, dp2, usbc, ...</param>
/// <param name="Name">Port label shown to the user.</param>
/// <param name="Code">Value sent with the protocol's command.</param>
public sealed record InputCandidate(string InputId, string Name, ushort Code);

/// <summary>One frame to send, then an optional pause.</summary>
public sealed record DdcStep(SetVcpFrame Frame, int DelayAfterMs = 0);

/// <summary>
/// Layer 2: decides WHAT bytes switch a monitor's input. Knows nothing about GPUs or drivers.
/// </summary>
/// <remarks>
/// To add a vendor: subclass <see cref="SetVcpProtocol"/> (if the vendor only changes the source
/// byte or VCP code) or this class (for multi-step sequences), then register it in
/// <see cref="ProtocolRegistry"/>. See docs/adding-a-protocol.md.
/// </remarks>
public abstract class InputProtocol
{
    /// <summary>Stable id used in profile files, e.g. "lg-alternative".</summary>
    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    /// <summary>
    /// True when frames need a transport that can write raw I2C (NvAPI, ADL, IGCL, IOAVService).
    /// OS-level VCP APIs such as Windows dxva2 can only send standard (source 0x51) commands.
    /// </summary>
    public abstract bool RequiresRawI2C { get; }

    /// <summary>Every code this protocol may send during the setup wizard, per port.</summary>
    public abstract IReadOnlyList<InputCandidate> Candidates { get; }

    /// <summary>Builds the frames that select the input with <paramref name="code"/>.</summary>
    public abstract IReadOnlyList<DdcStep> BuildSwitch(ushort code);

    /// <summary>
    /// How promising this protocol is for a monitor without a profile (0 = don't try by default).
    /// The wizard tries protocols in descending rank.
    /// </summary>
    public virtual int RankFor(MonitorIdentity monitor, MonitorCapabilities? capabilities) => 0;

    /// <summary>Candidates narrowed (or extended) using what the monitor reports about itself.</summary>
    public virtual IReadOnlyList<InputCandidate> CandidatesFor(MonitorCapabilities? capabilities) => Candidates;
}

/// <summary>
/// Base for protocols that select an input with a single "Set VCP Feature" frame. Most vendor
/// variants only differ in the source byte and VCP code, so a new one is a few lines.
/// </summary>
public abstract class SetVcpProtocol : InputProtocol
{
    public abstract byte SourceAddress { get; }

    public abstract byte VcpCode { get; }

    public override bool RequiresRawI2C => SourceAddress != DdcConstants.StandardSourceAddress;

    public override IReadOnlyList<DdcStep> BuildSwitch(ushort code) =>
        [new DdcStep(new SetVcpFrame(SourceAddress, VcpCode, code))];
}
