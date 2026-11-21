using MonitorSwitcher.Core.Ddc;
using MonitorSwitcher.Core.Monitors;

namespace MonitorSwitcher.Core.Protocols;

/// <summary>
/// LG's vendor input select: VCP 0xF4 sent with source byte 0x50 instead of 0x51. Needed on many
/// LG monitors (e.g. 32UN880) where the standard command blanks the screen or can't reach USB-C.
/// Requires a raw I2C transport because OS VCP APIs always use source 0x51.
/// </summary>
public sealed class LgAlternativeProtocol : SetVcpProtocol
{
    public const string ProtocolId = "lg-alternative";
    public const byte LgVcpCode = 0xF4;

    private static readonly InputCandidate[] KnownInputs =
    [
        new("hdmi1", "HDMI 1", 0x90),
        new("hdmi2", "HDMI 2", 0x91),
        new("hdmi3", "HDMI 3", 0x92),
        new("dp1", "DisplayPort 1", 0xD0),
        new("dp2", "DisplayPort 2", 0xD1),
        new("usbc", "USB-C", 0xD1),
        new("usbc", "USB-C", 0xD2),
        new("usbc", "USB-C", 0xD3),
    ];

    public override string Id => ProtocolId;

    public override string DisplayName => "LG alternative (VCP 0xF4, source 0x50)";

    public override byte SourceAddress => DdcConstants.LgAlternativeSourceAddress;

    public override byte VcpCode => LgVcpCode;

    public override IReadOnlyList<InputCandidate> Candidates => KnownInputs;

    public override int RankFor(MonitorIdentity monitor, MonitorCapabilities? capabilities)
    {
        if (!string.Equals(monitor.ManufacturerId, "GSM", StringComparison.Ordinal))
        {
            return 0;
        }
        return capabilities?.SupportsVcp(LgVcpCode) == true ? 70 : 50;
    }
}
