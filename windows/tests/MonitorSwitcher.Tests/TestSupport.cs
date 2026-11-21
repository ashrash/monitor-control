using MonitorSwitcher.Core.Ddc;
using MonitorSwitcher.Core.Logging;
using MonitorSwitcher.Core.Monitors;
using MonitorSwitcher.Core.Profiles;
using MonitorSwitcher.Core.Storage;
using MonitorSwitcher.Core.Switching;
using MonitorSwitcher.Core.Transports;

namespace MonitorSwitcher.Tests;

/// <summary>A transport that records what it would have sent.</summary>
internal sealed class FakeTransport(string id, bool raw, int priority = 10) : DdcTransport
{
    public List<(DisplayEndpoint Endpoint, SetVcpFrame Frame)> Sent { get; } = [];

    public List<DisplayEndpoint> Endpoints { get; } = [];

    public Queue<Exception> FailNext { get; } = new();

    public bool Readable { get; set; }

    public bool Answers { get; set; } = true;

    public VcpReading Reading { get; set; } = new(50, 100);

    public override string Id => id;

    public override string DisplayName => $"Fake {id}";

    public override bool SupportsRawI2C => raw;

    public override int Priority => priority;

    public override bool SupportsReads => Readable;

    public override TransportStatus Probe() => TransportStatus.Available("fake");

    public override IReadOnlyList<DisplayEndpoint> Enumerate() => Endpoints;

    public DisplayEndpoint Add(MonitorIdentity identity, string? osDisplayName = null)
    {
        var endpoint = new DisplayEndpoint(this, identity, $"{id}-{Endpoints.Count}", null) { OsDisplayName = osDisplayName };
        Endpoints.Add(endpoint);
        return endpoint;
    }

    protected override void SendCore(DisplayEndpoint endpoint, SetVcpFrame frame)
    {
        if (FailNext.TryDequeue(out var exception))
        {
            throw exception;
        }
        Sent.Add((endpoint, frame));
    }

    public override VcpReading? TryReadVcp(DisplayEndpoint endpoint, byte vcpCode) =>
        Readable && Answers ? Reading : null;
}

internal static class TestData
{
    public static readonly MonitorIdentity Lg32Un880 = new("GSM", 0x774F, 384592, "LG HDR 4K");
    public static readonly MonitorIdentity Dell = new("DEL", 0xA0F1, 1234, "DELL U2720Q");

    /// <summary>Capabilities string exactly as the 32UN880 returns it (note the missing "model(").</summary>
    public const string Lg32Un880Capabilities =
        "(prot(monitor)type(lcd)UN880cmds(01 02 03 0C E3 F3)vcp(02 04 05 08 10 12 14(05 08 0B ) 16 18 1A 52 60( 11 12 0F 00) AC AE B2 B6 C0 C6 C8 C9 D6(01 04) DF 62 8D F4 F5(00 01 02) F6(00 01 02) 4D 4E 4F 15(01 06 11 13 14 15 18 19 28 29 48) F7(00 01 02 03) F8(00 01) F9 E4 E5 E6 E7 E8 E9 EA EB EF FD(00 01) FE(00 01 02) FF)mccs_ver(2.1)mswhql(1))";

    public static string TestFile(params string[] parts) => Path.Combine([AppContext.BaseDirectory, .. parts]);

    public static ProfileCatalog BuiltInCatalog() => ProfileCatalog.Load(null, NullLog.Instance);

    public static MonitorProfile Profile(string id, string manufacturer, string protocol = "mccs-standard", string[]? productCodes = null,
        string? capabilitiesModel = null, string[]? modelNames = null, ProfileOrigin origin = ProfileOrigin.BuiltIn, ProfileQuirks? quirks = null,
        params (string Id, string Code, bool Verified)[] inputs)
    {
        return new MonitorProfile
        {
            Id = id,
            Name = id,
            Match = new ProfileMatch
            {
                Manufacturer = manufacturer,
                ProductCodes = productCodes?.ToList(),
                CapabilitiesModel = capabilitiesModel,
                ModelNames = modelNames?.ToList(),
            },
            Protocol = protocol,
            Inputs = (inputs.Length == 0 ? [("hdmi1", "0x11", false)] : inputs)
                .Select(i => new ProfileInput { Id = i.Id, Name = i.Id.ToUpperInvariant(), Code = i.Code, Verified = i.Verified })
                .ToList(),
            Quirks = quirks,
            Origin = origin,
        };
    }

    public static (SwitchService Service, string StatePath) Service(IReadOnlyList<DdcTransport> transports, ProfileCatalog? catalog = null)
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"ms-test-{Guid.NewGuid():N}", "state.json");
        var store = new JsonFileStore<AppState>(statePath, CoreJsonContext.Default.AppState, new InProcessLock(), NullLog.Instance);
        return (new SwitchService(transports, catalog ?? BuiltInCatalog(), store, new InProcessLock(), NullLog.Instance), statePath);
    }
}
