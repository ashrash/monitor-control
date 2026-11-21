using MonitorSwitcher.Core.Ddc;
using MonitorSwitcher.Core.Monitors;
using MonitorSwitcher.Core.Profiles;
using MonitorSwitcher.Core.Protocols;
using MonitorSwitcher.Core.Storage;
using MonitorSwitcher.Core.Switching;

namespace MonitorSwitcher.Tests;

public class SwitchServiceTests
{
    [Fact]
    public void Switching_sends_one_lg_frame_to_exactly_one_endpoint()
    {
        var nv = new FakeTransport("nvapi", raw: true);
        nv.Add(TestData.Lg32Un880, @"\\.\DISPLAY1");
        nv.Add(TestData.Dell, @"\\.\DISPLAY2");
        var (service, _) = TestData.Service([nv]);
        var lg = service.DiscoverMonitors(readCapabilities: false).Single(m => m.Identity.ManufacturerId == "GSM");

        var outcome = service.SwitchTo(lg, "usbc");

        var sent = Assert.Single(nv.Sent);
        Assert.Same(lg.Endpoints[0], sent.Endpoint);
        Assert.Equal(new SetVcpFrame(0x50, 0xF4, 0xD1), sent.Frame);
        Assert.Equal("usbc", outcome.InputId);
    }

    [Fact]
    public void Inputs_outside_the_profile_are_refused_before_anything_is_sent()
    {
        var nv = new FakeTransport("nvapi", raw: true);
        nv.Add(TestData.Lg32Un880);
        var (service, _) = TestData.Service([nv]);
        var lg = service.DiscoverMonitors(readCapabilities: false).Single();

        var ex = Assert.Throws<DdcException>(() => service.SwitchTo(lg, "hdmi7"));
        Assert.Equal(DdcErrorKind.InputNotInProfile, ex.Kind);
        Assert.Empty(nv.Sent);
    }

    [Fact]
    public void Lg_protocol_is_never_routed_through_a_vcp_only_transport()
    {
        var dxva2 = new FakeTransport("dxva2", raw: false, priority: 50);
        dxva2.Add(TestData.Lg32Un880);
        var (service, _) = TestData.Service([dxva2]);
        var lg = service.DiscoverMonitors(readCapabilities: false).Single();

        var ex = Assert.Throws<DdcException>(() => service.SwitchTo(lg, "usbc"));
        Assert.Equal(DdcErrorKind.UnsupportedByTransport, ex.Kind);
        Assert.Empty(dxva2.Sent);
    }

    [Fact]
    public void Endpoints_of_one_monitor_are_merged_and_the_raw_transport_is_preferred()
    {
        var nv = new FakeTransport("nvapi", raw: true, priority: 10);
        var dxva2 = new FakeTransport("dxva2", raw: false, priority: 50);
        dxva2.Add(TestData.Lg32Un880, @"\\.\DISPLAY1");
        nv.Add(TestData.Lg32Un880, @"\\.\DISPLAY1");
        var (service, _) = TestData.Service([dxva2, nv]);

        var lg = Assert.Single(service.DiscoverMonitors(readCapabilities: false));
        service.SwitchTo(lg, "usbc");

        Assert.Equal(2, lg.Endpoints.Count);
        Assert.Single(nv.Sent);
        Assert.Empty(dxva2.Sent);
    }

    [Fact]
    public void Endpoint_reported_without_serial_joins_the_same_monitor()
    {
        // Seen live: during an input switch one interface briefly reported the LG without its serial,
        // which made it look like two monitors and blocked the switch as ambiguous.
        var nv = new FakeTransport("nvapi", raw: true);
        var dxva2 = new FakeTransport("dxva2", raw: false, priority: 50);
        nv.Add(TestData.Lg32Un880, @"\\.\DISPLAY1");
        dxva2.Add(TestData.Lg32Un880 with { SerialNumber = 0 }, @"\\.\DISPLAY1");
        var (service, _) = TestData.Service([nv, dxva2]);

        var lg = Assert.Single(service.DiscoverMonitors(readCapabilities: false));
        Assert.Equal("GSM-774F-0005DE50", lg.StableId);
        Assert.Equal(2, lg.Endpoints.Count);
    }

    [Fact]
    public void Two_identical_models_with_different_serials_stay_separate()
    {
        var nv = new FakeTransport("nvapi", raw: true);
        nv.Add(TestData.Lg32Un880, @"\\.\DISPLAY1");
        nv.Add(TestData.Lg32Un880 with { SerialNumber = 999 }, @"\\.\DISPLAY2");
        var (service, _) = TestData.Service([nv]);

        Assert.Equal(2, service.DiscoverMonitors(readCapabilities: false).Count);
    }

    [Fact]
    public void Transient_failures_are_retried_per_profile_quirks()
    {
        var nv = new FakeTransport("nvapi", raw: true);
        nv.Add(TestData.Lg32Un880);
        nv.FailNext.Enqueue(new DdcException(DdcErrorKind.TransportFailure, "flaky"));
        var (service, _) = TestData.Service([nv]);
        var lg = service.DiscoverMonitors(readCapabilities: false).Single();

        var outcome = service.SwitchTo(lg, "usbc");

        Assert.Equal(2, outcome.Attempts);
        Assert.Single(nv.Sent);
    }

    [Fact]
    public void Safety_violations_are_never_retried()
    {
        var nv = new FakeTransport("nvapi", raw: true);
        nv.Add(TestData.Lg32Un880);
        nv.FailNext.Enqueue(new DdcSafetyException("blocked"));
        var (service, _) = TestData.Service([nv]);
        var lg = service.DiscoverMonitors(readCapabilities: false).Single();

        Assert.Throws<DdcSafetyException>(() => service.SwitchTo(lg, "usbc"));
        Assert.Empty(nv.Sent);
    }

    [Fact]
    public void Wizard_can_only_send_the_protocols_own_candidates()
    {
        var nv = new FakeTransport("nvapi", raw: true);
        nv.Add(TestData.Lg32Un880);
        var (service, _) = TestData.Service([nv]);
        var lg = service.DiscoverMonitors(readCapabilities: false).Single();
        var protocol = new LgAlternativeProtocol();

        Assert.Throws<DdcSafetyException>(() => service.SendCandidate(lg, protocol, new InputCandidate("usbc", "USB-C", 0x42)));
        service.SendCandidate(lg, protocol, protocol.Candidates.First(c => c.InputId == "usbc"));

        Assert.Single(nv.Sent);
    }

    [Fact]
    public void Last_sent_input_is_remembered_with_the_endpoint()
    {
        var nv = new FakeTransport("nvapi", raw: true);
        nv.Add(TestData.Lg32Un880);
        var (service, _) = TestData.Service([nv]);
        var lg = service.DiscoverMonitors(readCapabilities: false).Single();

        service.SwitchTo(lg, "usbc");

        var state = service.State.Load().Monitors[lg.StableId];
        Assert.Equal("usbc", state.LastInputId);
        Assert.Equal(TestData.Lg32Un880, state.Identity);
    }

    [Fact]
    public void Switching_to_this_computers_input_explains_why_it_cannot_work_when_the_monitor_is_away()
    {
        var nv = new FakeTransport("nvapi", raw: true);
        var dxva2 = new FakeTransport("dxva2", raw: false, priority: 50) { Readable = true, Answers = false };
        nv.Add(TestData.Lg32Un880);
        dxva2.Add(TestData.Lg32Un880);
        var (service, _) = TestData.Service([nv, dxva2]);
        var lg = service.DiscoverMonitors(readCapabilities: false).Single();
        var settings = new MonitorSettings { MonitorId = lg.StableId, LocalInputId = "hdmi2" };

        var ex = Assert.Throws<DdcException>(() => service.SwitchTo(lg, "hdmi2", settings));

        Assert.Equal(DdcErrorKind.MonitorOnOtherInput, ex.Kind);
        Assert.Empty(nv.Sent);
    }

    [Fact]
    public void Switching_to_this_computers_input_is_sent_when_the_monitor_answers()
    {
        var nv = new FakeTransport("nvapi", raw: true);
        var dxva2 = new FakeTransport("dxva2", raw: false, priority: 50) { Readable = true, Answers = true };
        nv.Add(TestData.Lg32Un880);
        dxva2.Add(TestData.Lg32Un880);
        var (service, _) = TestData.Service([nv, dxva2]);
        var lg = service.DiscoverMonitors(readCapabilities: false).Single();

        service.SwitchTo(lg, "hdmi2", new MonitorSettings { MonitorId = lg.StableId, LocalInputId = "hdmi2" });

        Assert.Single(nv.Sent);
    }

    [Fact]
    public void No_transport_available_is_a_clear_error()
    {
        var (service, _) = TestData.Service([]);
        var ex = Assert.Throws<DdcException>(() => service.DiscoverMonitors());
        Assert.Equal(DdcErrorKind.NoSupportedTransport, ex.Kind);
    }
}

public class ToggleLogicTests
{
    [Theory]
    [InlineData("hdmi2", null, "usbc")]
    [InlineData("usbc", null, "hdmi2")]
    [InlineData(null, "hdmi2", "usbc")]
    [InlineData(null, "usbc", "hdmi2")]
    [InlineData(null, null, "usbc")]
    public void Alternates_using_last_sent_then_local_input(string? last, string? local, string expected) =>
        Assert.Equal(expected, ToggleLogic.NextInput("hdmi2", "usbc", last, local));

    [Theory]
    [InlineData("usbc")]
    [InlineData("hdmi2")]
    [InlineData(null)]
    public void Monitor_that_only_obeys_the_active_computer_always_goes_to_the_other_input(string? last) =>
        Assert.Equal("usbc", ToggleLogic.NextInput("hdmi2", "usbc", last, "hdmi2", ignoresCommandsWhenInactive: true));

    [Fact]
    public void Toggle_inputs_come_from_settings_first()
    {
        var profile = TestData.Profile("p", "GSM", inputs: [("hdmi1", "0x90", true), ("hdmi2", "0x91", true), ("usbc", "0xD1", false)]);
        var settings = new MonitorSettings { ToggleInputs = ["hdmi1", "usbc"] };
        Assert.Equal(("hdmi1", "usbc"), ToggleLogic.ToggleInputs(profile, settings));
    }

    [Fact]
    public void Toggle_inputs_fall_back_to_the_two_verified_inputs()
    {
        var profile = TestData.Profile("p", "GSM", inputs: [("hdmi1", "0x90", false), ("hdmi2", "0x91", true), ("usbc", "0xD1", true)]);
        Assert.Equal(("hdmi2", "usbc"), ToggleLogic.ToggleInputs(profile, null));
    }

    [Fact]
    public void Toggle_inputs_are_unset_when_ambiguous()
    {
        var profile = TestData.Profile("p", "GSM", inputs: [("hdmi1", "0x90", false), ("hdmi2", "0x91", false), ("usbc", "0xD1", false)]);
        Assert.Null(ToggleLogic.ToggleInputs(profile, null));
    }

    [Fact]
    public void Invalid_toggle_settings_are_ignored()
    {
        var profile = TestData.Profile("p", "GSM", inputs: [("hdmi2", "0x91", true), ("usbc", "0xD1", true)]);
        Assert.Equal(("hdmi2", "usbc"), ToggleLogic.ToggleInputs(profile, new MonitorSettings { ToggleInputs = ["usbc", "usbc"] }));
    }
}

public class ResolverTests
{
    private static readonly MonitorProfile Lg = TestData.BuiltInCatalog().FindById("lg-32un880")!;

    [Theory]
    [InlineData("usbc", "usbc")]
    [InlineData("USB-C", "usbc")]
    [InlineData("Mac", "usbc")]
    [InlineData("pc", "hdmi2")]
    [InlineData("display", "dp1")]
    [InlineData("us", "usbc")]
    public void Inputs_resolve_by_id_name_label_or_unique_prefix(string query, string expected)
    {
        var settings = new MonitorSettings { InputLabels = new() { ["usbc"] = "Mac", ["hdmi2"] = "PC" } };
        Assert.Equal(expected, InputResolver.Resolve(Lg, settings, query).Id);
    }

    [Fact]
    public void Ambiguous_prefix_lists_the_options()
    {
        var ex = Assert.Throws<DdcException>(() => InputResolver.Resolve(Lg, null, "hdmi"));
        Assert.Contains("hdmi1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("hdmi2", ex.Message, StringComparison.Ordinal);
    }

    private static ResolvedMonitor Monitor(MonitorIdentity identity, string name)
    {
        var transport = new FakeTransport("t", raw: true);
        return new ResolvedMonitor
        {
            Identity = identity,
            Endpoints = [transport.Add(identity)],
            Profile = TestData.Profile(name, identity.ManufacturerId),
            Quality = MatchQuality.ProductCode,
        };
    }

    [Fact]
    public void Single_monitor_needs_no_query()
    {
        var lg = Monitor(TestData.Lg32Un880, "lg");
        Assert.Same(lg, MonitorResolver.Resolve([lg], new AppSettings(), null));
    }

    [Fact]
    public void Several_monitors_without_a_query_or_default_is_an_error_not_a_broadcast()
    {
        var monitors = new[] { Monitor(TestData.Lg32Un880, "lg"), Monitor(TestData.Dell, "dell") };
        var ex = Assert.Throws<DdcException>(() => MonitorResolver.Resolve(monitors, new AppSettings(), null));
        Assert.Equal(DdcErrorKind.AmbiguousMonitor, ex.Kind);
    }

    [Fact]
    public void Default_monitor_number_and_label_select_one_monitor()
    {
        var lg = Monitor(TestData.Lg32Un880, "lg");
        var dell = Monitor(TestData.Dell, "dell");
        var settings = new AppSettings { DefaultMonitorId = dell.StableId };
        settings.GetOrAddMonitor(lg.StableId).Label = "Desk";

        Assert.Same(dell, MonitorResolver.Resolve([lg, dell], settings, null));
        Assert.Same(lg, MonitorResolver.Resolve([lg, dell], settings, "1"));
        Assert.Same(lg, MonitorResolver.Resolve([lg, dell], settings, "desk"));
        Assert.Same(dell, MonitorResolver.Resolve([lg, dell], settings, "U2720"));
    }

    [Fact]
    public void No_monitors_is_a_clear_error()
    {
        var ex = Assert.Throws<DdcException>(() => MonitorResolver.Resolve([], new AppSettings(), null));
        Assert.Equal(DdcErrorKind.MonitorNotFound, ex.Kind);
        Assert.Contains("DDC/CI", ex.UserMessage, StringComparison.Ordinal);
    }
}
