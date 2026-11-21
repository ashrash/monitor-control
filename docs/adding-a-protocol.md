# Adding a vendor protocol

Add a protocol when a monitor needs bytes that the existing protocols can't produce, for
example a different VCP code, a different source byte, or a multi-step sequence. If the monitor
only needs different input *codes*, write a [profile](adding-a-profile.md) instead.

## 1. Windows (C#)

Most vendor variants are one "Set VCP Feature" frame with a different source byte or VCP code.
Subclass `SetVcpProtocol`:

```csharp
// windows/src/MonitorSwitcher.Core/Protocols/AcmeProtocol.cs
public sealed class AcmeProtocol : SetVcpProtocol
{
    public const string ProtocolId = "acme-alternative";

    private static readonly InputCandidate[] KnownInputs =
    [
        new("hdmi1", "HDMI 1", 0x21),
        new("usbc", "USB-C", 0x2B),
    ];

    public override string Id => ProtocolId;
    public override string DisplayName => "Acme alternative (VCP 0xE0)";
    public override byte SourceAddress => DdcConstants.StandardSourceAddress; // 0x51
    public override byte VcpCode => 0xE0;
    public override IReadOnlyList<InputCandidate> Candidates => KnownInputs;

    // Ranks the protocol for monitors without a profile (0 = don't try by default).
    public override int RankFor(MonitorIdentity monitor, MonitorCapabilities? caps) =>
        monitor.ManufacturerId == "ACM" ? 40 : 0;
}
```

For multi-step sequences, subclass `InputProtocol` and return several `DdcStep`s, each with an
optional delay, from `BuildSwitch`.

Then register it in `ProtocolRegistry.Protocols`, and add tests:

- packet bytes in `PacketTests`, ideally as a new vector in `spec/packet-vectors.json`;
- ranking and candidates in `ProtocolTests`.

### Safety rules

- **Source byte:** `DdcGuard` only allows 0x51 (standard) and 0x50 (LG). A new source byte must
  be added to `DdcGuard.AllowedSources` deliberately, with a comment explaining which vendor
  needs it and why it's safe. It is never a device address: the device address is always 0x6E.
- **Raw I2C:** protocols with a non-standard source byte automatically require a raw-I2C
  transport (`RequiresRawI2C`). They are never sent through dxva2.

## 2. macOS (Swift)

Mirror the class in `macos/Sources/MonitorSwitcherCore/Protocols/` and register it there too.
The shared vectors in `spec/packet-vectors.json` keep both implementations byte-identical.

## 3. Profiles

Reference the new protocol id from a profile's `protocol` field.
