using System.Text;
using System.Text.Json;
using MonitorSwitcher.Core;
using MonitorSwitcher.Core.Controls;
using MonitorSwitcher.Core.Ddc;
using MonitorSwitcher.Core.Logging;
using MonitorSwitcher.Core.Monitors;
using MonitorSwitcher.Core.Peer;
using MonitorSwitcher.Core.Storage;
using MonitorSwitcher.Core.Switching;
using MonitorSwitcher.Core.Transports;

namespace MonitorSwitcher.Tests;

public class ControlTests
{
    [Theory]
    [InlineData("70", false, 70)]
    [InlineData("70%", false, 70)]
    [InlineData("+10", true, 10)]
    [InlineData("-5", true, -5)]
    [InlineData("0", false, 0)]
    public void Parses_absolute_and_relative_changes(string text, bool relative, int value)
    {
        Assert.True(ControlChange.TryParse(text, out var change));
        Assert.Equal(new ControlChange(relative, value), change);
    }

    [Theory]
    [InlineData("")]
    [InlineData("150")]
    [InlineData("-101")]
    [InlineData("abc")]
    [InlineData("10.5")]
    [InlineData("%")]
    [InlineData("1-0")]
    public void Rejects_bad_levels(string text) => Assert.False(ControlChange.TryParse(text, out _));

    [Fact]
    public void Applies_and_clamps_changes()
    {
        Assert.Equal(100, ControlChange.Adjust(10).ApplyTo(95));
        Assert.Equal(0, ControlChange.Adjust(-10).ApplyTo(5));
        Assert.Null(ControlChange.Adjust(10).ApplyTo(null));
        Assert.Equal(40, ControlChange.Set(40).ApplyTo(null));
    }

    [Fact]
    public void Scales_to_the_monitors_range()
    {
        Assert.Equal(18, ControlMath.Percent(new VcpReading(18, 100)));
        Assert.Equal(50, ControlMath.Percent(new VcpReading(128, 255)));
        Assert.Equal(40, ControlMath.Percent(new VcpReading(40, 0)));
        Assert.Equal(128, ControlMath.Value(50, 255));
        Assert.Equal(70, ControlMath.Value(70, 100));
        Assert.Equal(100, ControlMath.Value(120, 100));
    }

    [Fact]
    public void Guard_allows_only_brightness_and_volume()
    {
        DdcGuard.EnsureControlFrame(new SetVcpFrame(0x51, 0x10, 50));
        DdcGuard.EnsureControlFrame(new SetVcpFrame(0x51, 0x62, 50));
        Assert.Throws<DdcSafetyException>(() => DdcGuard.EnsureControlFrame(new SetVcpFrame(0x51, 0x60, 0x11)));
        Assert.Throws<DdcSafetyException>(() => DdcGuard.EnsureControlFrame(new SetVcpFrame(0x50, 0x10, 50)));
        Assert.Throws<DdcSafetyException>(() => DdcGuard.EnsureControlFrame(new SetVcpFrame(0x51, 0xF4, 0x91)));
    }

    private static (SwitchService Service, ResolvedMonitor Monitor) LgMonitor(FakeTransport transport, string? capabilities = null)
    {
        transport.Add(TestData.Lg32Un880);
        var (service, _) = TestData.Service([transport]);
        var monitor = service.DiscoverMonitors(readCapabilities: false).Single();
        if (capabilities is not null)
        {
            monitor = new ResolvedMonitor
            {
                Identity = monitor.Identity,
                Endpoints = monitor.Endpoints,
                Profile = monitor.Profile,
                Quality = monitor.Quality,
                Capabilities = CapabilitiesParser.Parse(capabilities),
            };
        }
        return (service, monitor);
    }

    [Fact]
    public void Sets_an_absolute_level_scaled_to_the_maximum()
    {
        var transport = new FakeTransport("fake", raw: true) { Readable = true, Reading = new VcpReading(10, 200) };
        var (service, monitor) = LgMonitor(transport);

        var outcome = service.SetControl(monitor, MonitorControl.Brightness, ControlChange.Set(70));

        Assert.Equal(70, outcome.Percent);
        Assert.Equal([new SetVcpFrame(0x51, 0x10, 140)], transport.Sent.Select(s => s.Frame));
        Assert.Equal(70, service.State.Load().Monitors[monitor.StableId].Levels["Brightness"]);
    }

    [Fact]
    public void Steps_from_the_current_value()
    {
        var transport = new FakeTransport("fake", raw: true) { Readable = true };
        var (service, monitor) = LgMonitor(transport);

        Assert.Equal(40, service.SetControl(monitor, MonitorControl.Volume, ControlChange.Adjust(-10)).Percent);
        Assert.Equal(new SetVcpFrame(0x51, 0x62, 40), transport.Sent[^1].Frame);
    }

    [Fact]
    public void Step_without_a_reading_uses_the_last_value_set()
    {
        var transport = new FakeTransport("fake", raw: true);
        var (service, monitor) = LgMonitor(transport);

        var ex = Assert.Throws<DdcException>(() => service.SetControl(monitor, MonitorControl.Brightness, ControlChange.Adjust(10)));
        Assert.Equal(DdcErrorKind.MonitorNotResponding, ex.Kind);
        Assert.Empty(transport.Sent);

        service.SetControl(monitor, MonitorControl.Brightness, ControlChange.Set(30));
        Assert.Equal(40, service.SetControl(monitor, MonitorControl.Brightness, ControlChange.Adjust(10)).Percent);
    }

    [Fact]
    public void Refuses_a_control_the_monitor_does_not_list()
    {
        var transport = new FakeTransport("fake", raw: true);
        var (service, monitor) = LgMonitor(transport, "(vcp(10 60(11 12)))");

        Assert.Throws<DdcException>(() => service.SetControl(monitor, MonitorControl.Volume, ControlChange.Set(10)));
        Assert.Empty(transport.Sent);
        service.SetControl(monitor, MonitorControl.Brightness, ControlChange.Set(10));
    }
}

public class PeerWireTests
{
    private sealed record Message(string Json, string Mac, string Datagram);

    private sealed record Vectors(
        string PairingCode,
        string NormalizedCode,
        string KeyHex,
        string[] InvalidCodes,
        string[] EquivalentCodes,
        Message Request,
        Message Reply);

    private static Vectors Load()
    {
        var json = File.ReadAllText(TestData.TestFile("spec", "peer-vectors.json"));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Message Msg(string name) => new(
            root.GetProperty(name).GetProperty("json").GetString()!,
            root.GetProperty(name).GetProperty("mac").GetString()!,
            root.GetProperty(name).GetProperty("datagram").GetString()!);
        string[] List(string name) => [.. root.GetProperty(name).EnumerateArray().Select(e => e.GetString()!)];
        return new Vectors(
            root.GetProperty("pairingCode").GetString()!,
            root.GetProperty("normalizedCode").GetString()!,
            root.GetProperty("keyHex").GetString()!,
            List("invalidCodes"),
            List("equivalentCodes"),
            Msg("request"),
            Msg("reply"));
    }

    [Fact]
    public void Derives_the_shared_key()
    {
        var v = Load();
        Assert.Equal(v.NormalizedCode, PairingCode.Normalize(v.PairingCode));
        Assert.Equal(v.KeyHex, Convert.ToHexStringLower(PeerKey.FromPairingCode(v.PairingCode)!.Bytes));
        foreach (var code in v.EquivalentCodes)
        {
            Assert.Equal(PeerKey.FromPairingCode(v.PairingCode), PeerKey.FromPairingCode(code));
        }
        foreach (var code in v.InvalidCodes)
        {
            Assert.Null(PeerKey.FromPairingCode(code));
        }
    }

    [Fact]
    public void Accepts_the_golden_datagrams()
    {
        var v = Load();
        var key = PeerKey.FromPairingCode(v.PairingCode)!;
        Assert.Equal(v.Request.Mac, Convert.ToHexStringLower(key.Mac("request", Encoding.UTF8.GetBytes(v.Request.Json))));
        Assert.Equal(v.Reply.Mac, Convert.ToHexStringLower(key.Mac("reply", Encoding.UTF8.GetBytes(v.Reply.Json))));

        var request = PeerWire.DecodeRequest(Encoding.UTF8.GetBytes(v.Request.Datagram), key)!;
        Assert.Equal(PeerRequestKind.Switch, request.Kind);
        Assert.Equal("usbc", request.InputId);
        Assert.Equal(0x774F, request.Monitor!.ProductCode);
        Assert.Equal(384592u, request.Monitor.SerialNumber);
        var reply = PeerWire.DecodeReply(Encoding.UTF8.GetBytes(v.Reply.Datagram), key)!;
        Assert.True(reply.Ok);
        Assert.Equal("USB-C", reply.InputName);
    }

    [Fact]
    public void Rejects_wrong_key_label_or_tampering()
    {
        var v = Load();
        var key = PeerKey.FromPairingCode(v.PairingCode)!;
        var other = PeerKey.FromPairingCode(PairingCode.Generate())!;
        var datagram = Encoding.UTF8.GetBytes(v.Request.Datagram);

        Assert.Null(PeerWire.DecodeRequest(datagram, other));
        Assert.Null(PeerWire.DecodeReply(datagram, key)); // a request must not be accepted as a reply
        Assert.Null(PeerWire.DecodeRequest(Encoding.UTF8.GetBytes(v.Request.Datagram.Replace("usbc", "hdmi")), key));
        Assert.Null(PeerWire.DecodeRequest("MSW1 short"u8, key));
    }

    [Fact]
    public void Round_trips_own_messages()
    {
        var key = PeerKey.FromPairingCode(PairingCode.Generate())!;
        var request = new PeerRequest { Machine = "m", From = "PC", Kind = PeerRequestKind.Control, Control = "Volume", AdjustPercent = -10 };
        Assert.Equal(request, PeerWire.DecodeRequest(PeerWire.Encode(request, key), key));
    }

    [Fact]
    public void Generated_codes_are_valid_and_different()
    {
        var a = PairingCode.Generate();
        var b = PairingCode.Generate();
        Assert.NotEqual(a, b);
        Assert.Equal(PairingCode.Length, PairingCode.Normalize(a)!.Length);
        Assert.Equal(PairingCode.Length + PairingCode.Length / 4 - 1, a.Length);
    }

    [Fact]
    public void Replay_guard_refuses_stale_requests_and_remembers_replies()
    {
        var guard = new PeerReplayGuard();
        var now = DateTimeOffset.UtcNow;
        var request = new PeerRequest { Ts = now.ToUnixTimeSeconds() - 300, Machine = "m", From = "PC", Kind = PeerRequestKind.Ping };
        Assert.Equal(300, guard.ClockSkew(request, now));
        request = request with { Ts = now.ToUnixTimeSeconds() - 30 };
        Assert.Null(guard.ClockSkew(request, now));

        Assert.False(guard.IsDuplicate(request, out _));
        guard.Remember(request, [1, 2, 3], now);
        Assert.True(guard.IsDuplicate(request, out var cached));
        Assert.Equal([1, 2, 3], cached);
    }
}

public class PeerMonitorMatchingTests
{
    [Fact]
    public void Matches_the_same_monitor_under_another_product_code()
    {
        // The PC sees the 32UN880 as 0x774F, the Mac as 0x7750; the serial is the same.
        var viaMac = TestData.Lg32Un880 with { ProductCode = 0x7750 };
        var transport = new FakeTransport("fake", raw: true);
        transport.Add(TestData.Dell);
        transport.Add(viaMac);
        var monitors = TestData.Service([transport]).Service.DiscoverMonitors(readCapabilities: false);

        var reference = new PeerMonitorRef { ManufacturerId = "GSM", ProductCode = 0x774F, SerialNumber = 384592, ProfileId = "lg-32un880" };
        Assert.Equal(viaMac.StableId, reference.Find(monitors)?.StableId);
    }

    [Fact]
    public void Falls_back_to_the_profile_only_when_unambiguous()
    {
        var noSerial = new MonitorIdentity("GSM", 0x774F, 0, "LG HDR 4K");
        var transport = new FakeTransport("fake", raw: true);
        transport.Add(noSerial);
        var monitors = TestData.Service([transport]).Service.DiscoverMonitors(readCapabilities: false);

        Assert.NotNull(new PeerMonitorRef { ManufacturerId = "GSM", ProductCode = 0x7750, ProfileId = "lg-32un880" }.Find(monitors));
        Assert.Null(new PeerMonitorRef { ManufacturerId = "GSM", ProductCode = 0x7750 }.Find(monitors));
        Assert.Null(new PeerMonitorRef { ManufacturerId = "GSM", ProductCode = 0x774F, SerialNumber = 999 }.Find(monitors));
    }
}

public class MonitorActionsTests
{
    private sealed class FakeChannel : IPeerChannel
    {
        public List<PeerRequest> Requests { get; } = [];

        public Func<PeerRequest, PeerReply> Answer { get; set; } =
            r => new PeerReply { Id = r.Id, From = "Mac", Ok = true, Message = "Done.", Percent = 30 };

        public PeerReply Send(PeerRequest request)
        {
            Requests.Add(request);
            return Answer(request);
        }
    }

    private static (MonitorActions Actions, FakeTransport Transport, FakeChannel Channel, ResolvedMonitor Monitor) Create(bool answers, bool paired)
    {
        var transport = new FakeTransport("fake", raw: true) { Readable = true, Answers = answers };
        transport.Add(TestData.Lg32Un880);
        var (service, statePath) = TestData.Service([transport]);
        var root = Path.GetDirectoryName(statePath)!;
        var services = new AppServices
        {
            Paths = new AppPaths(root),
            Log = NullLog.Instance,
            Transports = [transport],
            Settings = new JsonFileStore<AppSettings>(Path.Combine(root, "settings.json"), CoreJsonContext.Default.AppSettings, new InProcessLock(), NullLog.Instance),
            State = service.State,
            Switcher = service,
        };
        var channel = new FakeChannel();
        var actions = new MonitorActions(services, new PeerMachine("pc", "PC"), paired ? channel : null);
        return (actions, transport, channel, service.DiscoverMonitors(readCapabilities: false).Single());
    }

    private static MonitorSettings PcSettings(ResolvedMonitor monitor) =>
        new() { MonitorId = monitor.StableId, LocalInputId = "hdmi2", ToggleInputs = ["usbc", "hdmi2"] };

    [Fact]
    public void Acts_locally_while_the_monitor_answers()
    {
        var (actions, transport, channel, monitor) = Create(answers: true, paired: true);

        var result = actions.Toggle(monitor, PcSettings(monitor));

        Assert.Null(result.ViaPeer);
        Assert.Equal("usbc", result.InputId);
        Assert.Equal([new SetVcpFrame(0x50, 0xF4, 0xD1)], transport.Sent.Select(s => s.Frame));
        Assert.Empty(channel.Requests);
    }

    [Fact]
    public void Toggle_asks_the_peer_to_come_back_when_the_monitor_ignores_us()
    {
        var (actions, transport, channel, monitor) = Create(answers: false, paired: true);

        var result = actions.Toggle(monitor, PcSettings(monitor));

        Assert.Equal("Mac", result.ViaPeer);
        Assert.Equal("hdmi2", result.InputId);
        Assert.Empty(transport.Sent); // nothing is sent to a monitor that ignores us
        var request = Assert.Single(channel.Requests);
        Assert.Equal(PeerRequestKind.Switch, request.Kind);
        Assert.Equal("hdmi2", request.InputId);
        Assert.Equal(384592u, request.Monitor!.SerialNumber);
        Assert.Equal("hdmi2", actions.Services.State.Load().Monitors[monitor.StableId].LastInputId);
    }

    [Fact]
    public void Brightness_is_relayed_too()
    {
        var (actions, _, channel, monitor) = Create(answers: false, paired: true);

        var result = actions.SetControl(monitor, MonitorControl.Brightness, ControlChange.Adjust(10));

        Assert.Equal(30, result.Percent);
        Assert.Equal("Brightness", channel.Requests[0].Control);
        Assert.Equal(10, channel.Requests[0].AdjustPercent);
        Assert.Null(channel.Requests[0].SetPercent);
    }

    [Fact]
    public void Unpaired_computer_explains_instead_of_relaying()
    {
        var (actions, transport, _, monitor) = Create(answers: false, paired: false);

        var ex = Assert.Throws<DdcException>(() => actions.SwitchInput(monitor, "hdmi2", PcSettings(monitor)));
        Assert.Equal(DdcErrorKind.MonitorOnOtherInput, ex.Kind);
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public void A_refusal_from_the_peer_is_an_error()
    {
        var (actions, _, channel, monitor) = Create(answers: false, paired: true);
        channel.Answer = r => new PeerReply { Id = r.Id, From = "Mac", Ok = false, Message = "Not connected." };

        var ex = Assert.Throws<DdcException>(() => actions.SwitchInput(monitor, "hdmi2", null));
        Assert.Equal(DdcErrorKind.PeerUnavailable, ex.Kind);
    }

    [Fact]
    public void Handles_a_request_from_the_peer_with_its_own_profile()
    {
        var (actions, transport, channel, monitor) = Create(answers: true, paired: true);
        var reference = PeerMonitorRef.From(monitor) with { ProductCode = 0x7750 };

        var reply = actions.Handle(new PeerRequest { Machine = "mac", From = "Mac", Kind = PeerRequestKind.Switch, Monitor = reference, InputId = "usbc" }, new AppSettings());
        Assert.True(reply.Ok, reply.Message);
        Assert.Equal("USB-C", reply.InputName);
        Assert.Equal(new SetVcpFrame(0x50, 0xF4, 0xD1), transport.Sent[^1].Frame);

        var bad = actions.Handle(new PeerRequest { Machine = "mac", From = "Mac", Kind = PeerRequestKind.Switch, Monitor = reference, InputId = "vga1" }, new AppSettings());
        Assert.False(bad.Ok);
        Assert.Empty(channel.Requests); // a handled request is never relayed again
    }

    [Fact]
    public void Peer_messages_use_the_same_json_names_as_the_mac()
    {
        var request = new PeerRequest
        {
            Id = "i", Ts = 1, Machine = "m", From = "PC", Kind = PeerRequestKind.Switch, InputId = "usbc",
            Monitor = new PeerMonitorRef { ManufacturerId = "GSM", ProductCode = 0x774F, SerialNumber = 384592 },
        };
        var json = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(request, PeerJsonContext.Default.PeerRequest));
        Assert.Equal("""{"v":1,"id":"i","ts":1,"machine":"m","from":"PC","kind":"switch","monitor":{"manufacturerId":"GSM","productCode":30543,"serialNumber":384592},"inputId":"usbc"}""", json);
    }
}
