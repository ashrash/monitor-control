using MonitorSwitcher.Core.Monitors;
using MonitorSwitcher.Core.Profiles;
using MonitorSwitcher.Core.Protocols;
using MonitorSwitcher.Core.Setup;
using MonitorSwitcher.Core.Switching;

namespace MonitorSwitcher.Tests;

public class SetupPlannerTests
{
    private static ResolvedMonitor Discover(MonitorIdentity identity, bool raw = true, string? capabilities = null)
    {
        var transport = new FakeTransport(raw ? "nvapi" : "dxva2", raw);
        transport.Add(identity);
        var (service, _) = TestData.Service([transport]);
        var monitor = service.DiscoverMonitors(readCapabilities: false).Single();
        if (capabilities is null)
        {
            return monitor;
        }

        var caps = CapabilitiesParser.Parse(capabilities);
        var match = service.Catalog.Resolve(identity, caps);
        return new ResolvedMonitor
        {
            Identity = monitor.Identity,
            Endpoints = monitor.Endpoints,
            Profile = match.Profile,
            Quality = match.Quality,
            Capabilities = caps,
        };
    }

    [Fact]
    public void Known_lg_tries_its_profile_code_first_and_returns_with_the_profile_code()
    {
        var lg = Discover(TestData.Lg32Un880);

        var attempts = SetupPlanner.Plan(lg, "hdmi2", "usbc");

        var first = attempts[0];
        Assert.Equal(LgAlternativeProtocol.ProtocolId, first.Protocol.Id);
        Assert.Equal(0xD1, first.Target.Code);
        Assert.Equal(0x91, first.Return!.Code);
        Assert.Equal([0xD1, 0xD2, 0xD3], attempts.Where(a => a.Protocol.Id == LgAlternativeProtocol.ProtocolId).Select(a => (int)a.Target.Code));
    }

    [Fact]
    public void With_lg_capabilities_the_standard_protocol_adds_no_usb_c_guesses()
    {
        var lg = Discover(TestData.Lg32Un880, capabilities: TestData.Lg32Un880Capabilities);
        var attempts = SetupPlanner.Plan(lg, "hdmi2", "usbc");
        Assert.All(attempts, a => Assert.Equal(LgAlternativeProtocol.ProtocolId, a.Protocol.Id));
    }

    [Fact]
    public void Every_planned_code_comes_from_the_profile_or_the_protocols_candidates()
    {
        var lg = Discover(TestData.Lg32Un880);
        foreach (var attempt in SetupPlanner.Plan(lg, "hdmi2", "usbc"))
        {
            var allowed = attempt.Protocol.Candidates.Select(c => c.Code)
                .Concat(attempt.Protocol.Id == lg.Profile.Protocol ? lg.Profile.Inputs.Select(i => i.CodeValue) : []);
            Assert.Contains(attempt.Target.Code, allowed);
        }
    }

    [Fact]
    public void Unknown_monitor_uses_codes_its_capabilities_list()
    {
        var dell = Discover(TestData.Dell, capabilities: "(model(U2720Q)vcp(60(0F 11 1B)))");

        var attempt = Assert.Single(SetupPlanner.Plan(dell, "hdmi1", "usbc"));

        Assert.Equal(StandardMccsProtocol.ProtocolId, attempt.Protocol.Id);
        Assert.Equal(0x1B, attempt.Target.Code);
        Assert.Equal(0x11, attempt.Return!.Code);
    }

    [Fact]
    public void Vendor_protocols_are_skipped_when_only_a_vcp_transport_reaches_the_monitor()
    {
        var lg = Discover(TestData.Lg32Un880, raw: false);
        Assert.DoesNotContain(SetupPlanner.UsableProtocols(lg), p => p.RequiresRawI2C);
    }

    [Fact]
    public void Port_choices_for_a_known_monitor_are_its_profile_inputs()
    {
        var lg = Discover(TestData.Lg32Un880);
        Assert.Equal(["hdmi1", "hdmi2", "dp1", "usbc"], SetupPlanner.PortChoices(lg).Select(p => p.InputId));
    }

    [Fact]
    public void Confirming_built_in_codes_needs_no_user_profile()
    {
        var lg = Discover(TestData.Lg32Un880);
        var confirmed = new[] { new ConfirmedInput("usbc", "USB-C", 0xD1, true), new ConfirmedInput("hdmi2", "HDMI 2", 0x91, false) };

        Assert.False(SetupPlanner.NeedsUserProfile(lg, new LgAlternativeProtocol(), confirmed, ignoresCommandsWhenInactive: null));
        Assert.False(SetupPlanner.NeedsUserProfile(lg, new LgAlternativeProtocol(), confirmed, ignoresCommandsWhenInactive: true));
        Assert.True(SetupPlanner.NeedsUserProfile(lg, new LgAlternativeProtocol(), confirmed, ignoresCommandsWhenInactive: false));
        Assert.True(SetupPlanner.NeedsUserProfile(lg, new LgAlternativeProtocol(), [new ConfirmedInput("usbc", "USB-C", 0xD2, true)], null));
        Assert.True(SetupPlanner.NeedsUserProfile(lg, new StandardMccsProtocol(), confirmed, null));
    }

    [Fact]
    public void User_profile_for_an_unknown_monitor_is_valid_and_specific()
    {
        var dell = Discover(TestData.Dell, capabilities: "(vcp(60(0F 11 1B)))");
        var confirmed = new[] { new ConfirmedInput("usbc", "USB-C", 0x1B, true), new ConfirmedInput("hdmi1", "HDMI 1", 0x11, true) };

        var profile = SetupPlanner.BuildUserProfile(dell, new StandardMccsProtocol(), confirmed, ignoresCommandsWhenInactive: false, existingUserProfile: null);

        Assert.Empty(ProfileValidator.Validate(profile));
        Assert.Equal("del-a0f1", profile.Id);
        Assert.Equal(["0xA0F1"], profile.Match.ProductCodes!);
        Assert.Null(profile.Match.CapabilitiesModel);
        Assert.Equal(["usbc", "hdmi1"], profile.Inputs.Select(i => i.Id));
        Assert.All(profile.Inputs, i => Assert.True(i.Verified));

        // And it round-trips through the same parser as hand-written profiles.
        var parsed = ProfileCatalog.Parse(ProfileCatalog.Serialize(profile), ProfileOrigin.User, "x");
        Assert.Equal(StandardMccsProtocol.ProtocolId, parsed.Protocol);
    }

    [Fact]
    public void User_profile_built_on_a_known_model_keeps_its_other_inputs_and_quirks()
    {
        var lg = Discover(TestData.Lg32Un880);
        var confirmed = new[] { new ConfirmedInput("usbc", "USB-C", 0xD2, true) };

        var profile = SetupPlanner.BuildUserProfile(lg, new LgAlternativeProtocol(), confirmed, ignoresCommandsWhenInactive: null, existingUserProfile: null);

        Assert.Empty(ProfileValidator.Validate(profile));
        Assert.Equal("0xD2", profile.FindInput("usbc")!.Code);
        Assert.Equal("0x91", profile.FindInput("hdmi2")!.Code);
        Assert.True(profile.EffectiveQuirks.IgnoresCommandsWhenInactive);
        Assert.Equal(lg.Profile.Name, profile.Name);
    }

    [Fact]
    public void User_profile_wins_over_the_built_in_profile_once_saved()
    {
        var lg = Discover(TestData.Lg32Un880);
        var profile = SetupPlanner.BuildUserProfile(lg, new LgAlternativeProtocol(), [new ConfirmedInput("usbc", "USB-C", 0xD2, true)], null, null);
        var catalog = new ProfileCatalog([.. TestData.BuiltInCatalog().Profiles, profile], []);

        Assert.Equal(profile.Id, catalog.Resolve(TestData.Lg32Un880, null).Profile.Id);
    }
}
