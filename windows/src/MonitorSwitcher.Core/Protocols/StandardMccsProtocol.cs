using MonitorSwitcher.Core.Ddc;
using MonitorSwitcher.Core.Monitors;

namespace MonitorSwitcher.Core.Protocols;

/// <summary>
/// VESA MCCS "Input Select" (VCP 0x60, source 0x51). Works on most monitors, and through any
/// transport including Windows dxva2.
/// </summary>
public sealed class StandardMccsProtocol : SetVcpProtocol
{
    public const string ProtocolId = "mccs-standard";

    private static readonly InputCandidate[] KnownInputs =
    [
        new("hdmi1", "HDMI 1", 0x11),
        new("hdmi2", "HDMI 2", 0x12),
        new("dp1", "DisplayPort 1", 0x0F),
        new("dp2", "DisplayPort 2", 0x10),
        new("usbc", "USB-C", 0x1B),       // common vendor extension (Dell and others)
        new("usbc", "USB-C", 0x10),       // some monitors expose USB-C as DisplayPort 2
        new("dvi1", "DVI 1", 0x03),
        new("dvi2", "DVI 2", 0x04),
        new("vga1", "VGA", 0x01),
    ];

    public override string Id => ProtocolId;

    public override string DisplayName => "Standard MCCS (VCP 0x60)";

    public override byte SourceAddress => DdcConstants.StandardSourceAddress;

    public override byte VcpCode => DdcConstants.InputSelectVcp;

    public override IReadOnlyList<InputCandidate> Candidates => KnownInputs;

    public override int RankFor(MonitorIdentity monitor, MonitorCapabilities? capabilities) =>
        capabilities is null || capabilities.SupportsVcp(DdcConstants.InputSelectVcp) ? 10 : 1;

    /// <summary>
    /// If the monitor lists its 0x60 values, only those are tried; unknown listed values are added
    /// as generic "Input 0xNN" candidates so the wizard can still offer them.
    /// </summary>
    public override IReadOnlyList<InputCandidate> CandidatesFor(MonitorCapabilities? capabilities)
    {
        var listed = capabilities?.ValuesFor(DdcConstants.InputSelectVcp).Where(v => v != 0).ToHashSet();
        if (listed is null || listed.Count == 0)
        {
            return KnownInputs;
        }

        var result = KnownInputs.Where(c => listed.Contains(c.Code)).ToList();
        foreach (var code in listed.Where(code => KnownInputs.All(c => c.Code != code)).Order())
        {
            result.Add(new InputCandidate($"input-{code:x2}", $"Input {Hex.Format(code)}", code));
        }
        return result;
    }
}
