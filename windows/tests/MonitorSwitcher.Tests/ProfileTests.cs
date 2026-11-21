using System.Text.Json;
using MonitorSwitcher.Core.Logging;
using MonitorSwitcher.Core.Monitors;
using MonitorSwitcher.Core.Profiles;
using MonitorSwitcher.Core.Protocols;

namespace MonitorSwitcher.Tests;

public class ProfileMatchingTests
{
    [Fact]
    public void Lg_32un880_matches_its_built_in_profile_by_product_code()
    {
        var match = TestData.BuiltInCatalog().Resolve(TestData.Lg32Un880, capabilities: null);

        Assert.Equal("lg-32un880", match.Profile.Id);
        Assert.Equal(MatchQuality.ProductCode, match.Quality);
        Assert.Equal(LgAlternativeProtocol.ProtocolId, match.Profile.Protocol);
        Assert.True(match.Profile.EffectiveQuirks.IgnoresCommandsWhenInactive);
        Assert.Equal("0xD1", match.Profile.FindInput("usbc")!.Code);
    }

    [Fact]
    public void Mac_connection_product_code_matches_by_product_code()
    {
        // Over USB-C/DP to a Mac the same 32UN880 reports 0x7750 instead of 0x774F.
        var viaMac = TestData.Lg32Un880 with { ProductCode = 0x7750 };

        var match = TestData.BuiltInCatalog().Resolve(viaMac, null);

        Assert.Equal("lg-32un880", match.Profile.Id);
        Assert.Equal(MatchQuality.ProductCode, match.Quality);
    }

    [Fact]
    public void Sibling_model_matches_by_capabilities_model_when_product_code_differs()
    {
        var sibling = TestData.Lg32Un880 with { ProductCode = 0x7700 };
        var caps = CapabilitiesParser.Parse(TestData.Lg32Un880Capabilities);

        var match = TestData.BuiltInCatalog().Resolve(sibling, caps);

        Assert.Equal("lg-32un880", match.Profile.Id);
        Assert.Equal(MatchQuality.CapabilitiesModel, match.Quality);
    }

    [Fact]
    public void Generic_lg_edid_name_alone_does_not_match_the_32un880()
    {
        // Many LG models are all called "LG HDR 4K"; the profile must not grab them.
        var otherLg = new MonitorIdentity("GSM", 0x5B08, 1, "LG HDR 4K");

        var match = TestData.BuiltInCatalog().Resolve(otherLg, capabilities: null);

        Assert.Equal(ProfileOrigin.Fallback, match.Profile.Origin);
        Assert.Equal(MatchQuality.Fallback, match.Quality);
    }

    [Fact]
    public void Unknown_monitor_gets_a_standard_mccs_fallback_from_its_capabilities()
    {
        var caps = CapabilitiesParser.Parse("(model(U2720Q)vcp(60(0F 11 1B)))");

        var match = TestData.BuiltInCatalog().Resolve(TestData.Dell, caps);

        Assert.Equal(ProfileOrigin.Fallback, match.Profile.Origin);
        Assert.Equal(StandardMccsProtocol.ProtocolId, match.Profile.Protocol);
        Assert.Equal(["dp1", "hdmi1", "usbc"], match.Profile.Inputs.Select(i => i.Id).Order());
        Assert.All(match.Profile.Inputs, i => Assert.False(i.Verified));
        Assert.Empty(ProfileValidator.Validate(match.Profile));
    }

    [Fact]
    public void Manufacturer_must_match_even_if_the_product_code_does()
    {
        var profile = TestData.Profile("x", "GSM", productCodes: ["0xA0F1"]);
        Assert.Equal(MatchQuality.None, ProfileMatcher.Evaluate(profile, TestData.Dell, null));
    }

    [Fact]
    public void Product_code_beats_model_name()
    {
        var byName = TestData.Profile("by-name", "DEL", modelNames: ["DELL U2720Q"]);
        var byCode = TestData.Profile("by-code", "DEL", productCodes: ["0xA0F1"]);

        var best = ProfileMatcher.FindBest([byName, byCode], TestData.Dell, null);

        Assert.Equal("by-code", best!.Profile.Id);
        Assert.Equal(MatchQuality.ProductCode, best.Quality);
    }

    [Fact]
    public void User_profile_wins_a_tie_with_a_built_in_profile()
    {
        var builtIn = TestData.Profile("a-builtin", "DEL", productCodes: ["0xA0F1"]);
        var user = TestData.Profile("z-user", "DEL", productCodes: ["0xA0F1"], origin: ProfileOrigin.User);

        Assert.Equal("z-user", ProfileMatcher.FindBest([builtIn, user], TestData.Dell, null)!.Profile.Id);
    }

    [Fact]
    public void Forced_profile_id_from_settings_overrides_matching()
    {
        var catalog = new ProfileCatalog([TestData.Profile("forced", "XXX", productCodes: ["0x0001"])], []);
        var match = catalog.Resolve(TestData.Dell, null, forcedProfileId: "forced");
        Assert.Equal("forced", match.Profile.Id);
    }
}

public class ProfileFileTests
{
    private static IEnumerable<string> ProfilePaths() =>
        Directory.EnumerateFiles(TestData.TestFile("profiles"), "*.json", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".schema.json", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(TestData.TestFile("profiles"), f));

    public static TheoryData<string> ProfileFiles() => new(ProfilePaths());

    [Theory]
    [MemberData(nameof(ProfileFiles))]
    public void Every_shipped_profile_is_valid_and_named_after_its_id(string relativePath)
    {
        var path = TestData.TestFile("profiles", relativePath);
        var profile = ProfileCatalog.Parse(File.ReadAllText(path), ProfileOrigin.BuiltIn, relativePath);

        Assert.Equal(Path.GetFileNameWithoutExtension(path), profile.Id);
        Assert.StartsWith(Path.GetFileName(Path.GetDirectoryName(path))! + "-", profile.Id, StringComparison.Ordinal);
        Assert.Equal("../profile.schema.json", profile.Schema);
    }

    [Fact]
    public void Built_in_catalog_loads_without_errors()
    {
        var catalog = ProfileCatalog.Load(null, NullLog.Instance);
        Assert.Empty(catalog.Errors);
        Assert.Contains(catalog.Profiles, p => p.Id == "lg-32un880");
        Assert.Equal(catalog.Profiles.Count, catalog.Profiles.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void Embedded_profiles_match_the_files_in_the_repository()
    {
        var embedded = ProfileCatalog.Load(null, NullLog.Instance).Profiles.Select(p => p.Id).Order();
        var files = ProfilePaths().Select(Path.GetFileNameWithoutExtension).Order();
        Assert.Equal(files, embedded);
    }

    [Fact]
    public void User_profiles_folder_is_loaded_and_bad_files_are_reported_not_thrown()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ms-profiles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "dell-u2720q.json"), """
                { "id": "dell-u2720q", "name": "Dell U2720Q", "match": { "manufacturer": "DEL", "productCodes": ["0xA0F1"] },
                  "protocol": "mccs-standard", "inputs": [ { "id": "usbc", "name": "USB-C", "code": "0x1B" } ] }
                """);
            File.WriteAllText(Path.Combine(dir, "broken.json"), "{ not json");

            var catalog = ProfileCatalog.Load(dir, NullLog.Instance);

            Assert.Contains(catalog.Profiles, p => p.Id == "dell-u2720q" && p.Origin == ProfileOrigin.User);
            Assert.Single(catalog.Errors);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class ProfileValidationTests
{
    private const string Valid = """
        { "id": "acme-x1", "name": "Acme X1", "match": { "manufacturer": "ACM", "productCodes": ["0x0001"] },
          "protocol": "mccs-standard", "inputs": [ { "id": "hdmi1", "name": "HDMI 1", "code": "0x11" } ] }
        """;

    private static IReadOnlyList<string> Errors(string json)
    {
        try
        {
            ProfileCatalog.Parse(json, ProfileOrigin.User, "test.json");
            return [];
        }
        catch (ProfileFormatException ex)
        {
            return ex.Errors;
        }
    }

    [Fact]
    public void Valid_profile_parses() => Assert.Empty(Errors(Valid));

    [Theory]
    [InlineData("\"id\": \"acme-x1\"", "\"id\": \"Acme X1\"", "id")]
    [InlineData("\"manufacturer\": \"ACM\"", "\"manufacturer\": \"acme\"", "manufacturer")]
    [InlineData("\"protocol\": \"mccs-standard\"", "\"protocol\": \"made-up\"", "protocol")]
    [InlineData("\"code\": \"0x11\"", "\"code\": \"11\"", "code")]
    [InlineData("\"code\": \"0x11\"", "\"code\": \"0x10000\"", "code")]
    [InlineData("\"productCodes\": [\"0x0001\"]", "\"productCodes\": [\"774F\"]", "productCodes")]
    public void Invalid_fields_are_reported(string find, string replace, string expectedWord)
    {
        var errors = Errors(Valid.Replace(find, replace, StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains(expectedWord, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_profile_may_not_claim_every_monitor_of_a_manufacturer()
    {
        var errors = Errors(Valid.Replace(", \"productCodes\": [\"0x0001\"]", "", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("productCodes, capabilitiesModel or modelNames", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_input_ids_are_rejected()
    {
        var json = Valid.Replace(
            "[ { \"id\": \"hdmi1\", \"name\": \"HDMI 1\", \"code\": \"0x11\" } ]",
            "[ { \"id\": \"hdmi1\", \"name\": \"HDMI 1\", \"code\": \"0x11\" }, { \"id\": \"hdmi1\", \"name\": \"Again\", \"code\": \"0x12\" } ]",
            StringComparison.Ordinal);
        Assert.Contains(Errors(json), e => e.Contains("more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_properties_are_rejected_to_catch_typos()
    {
        var json = Valid.Replace("\"productCodes\"", "\"productCode\"", StringComparison.Ordinal);
        Assert.NotEmpty(Errors(json));
    }

    [Fact]
    public void Profiles_round_trip_through_json()
    {
        var profile = ProfileCatalog.Parse(Valid, ProfileOrigin.User, "x");
        var again = ProfileCatalog.Parse(ProfileCatalog.Serialize(profile), ProfileOrigin.User, "y");
        Assert.Equal(JsonSerializer.Serialize(profile.Inputs.Select(i => i.Code)), JsonSerializer.Serialize(again.Inputs.Select(i => i.Code)));
        Assert.Equal(profile.Match.ProductCodes, again.Match.ProductCodes);
    }
}

public class ProtocolTests
{
    [Fact]
    public void Lg_alternative_builds_source_0x50_vcp_0xF4()
    {
        var steps = new LgAlternativeProtocol().BuildSwitch(0xD1);
        var frame = Assert.Single(steps).Frame;
        Assert.Equal(0x50, frame.SourceAddress);
        Assert.Equal(0xF4, frame.VcpCode);
        Assert.Equal(0xD1, frame.Value);
    }

    [Fact]
    public void Standard_builds_source_0x51_vcp_0x60()
    {
        var frame = Assert.Single(new StandardMccsProtocol().BuildSwitch(0x12)).Frame;
        Assert.Equal(0x51, frame.SourceAddress);
        Assert.Equal(0x60, frame.VcpCode);
    }

    [Fact]
    public void Only_vendor_protocols_require_raw_i2c()
    {
        Assert.True(new LgAlternativeProtocol().RequiresRawI2C);
        Assert.False(new StandardMccsProtocol().RequiresRawI2C);
    }

    [Fact]
    public void Lg_monitors_try_the_lg_protocol_first()
    {
        var caps = CapabilitiesParser.Parse(TestData.Lg32Un880Capabilities);
        var ranked = ProtocolRegistry.RankedFor(TestData.Lg32Un880, caps);
        Assert.Equal([LgAlternativeProtocol.ProtocolId, StandardMccsProtocol.ProtocolId], ranked.Select(p => p.Id));
    }

    [Fact]
    public void Other_brands_only_try_the_standard_protocol()
    {
        var ranked = ProtocolRegistry.RankedFor(TestData.Dell, null);
        Assert.Equal([StandardMccsProtocol.ProtocolId], ranked.Select(p => p.Id));
    }

    [Fact]
    public void Standard_candidates_follow_the_monitors_capabilities()
    {
        // The 32UN880 lists 0x60 values 11 12 0F 00: no USB-C, which is why the standard method can't reach it.
        var caps = CapabilitiesParser.Parse(TestData.Lg32Un880Capabilities);
        var candidates = new StandardMccsProtocol().CandidatesFor(caps);
        Assert.Equal(["dp1", "hdmi1", "hdmi2"], candidates.Select(c => c.InputId).Order());
    }

    [Fact]
    public void Unlisted_codes_from_capabilities_become_generic_candidates()
    {
        var caps = CapabilitiesParser.Parse("vcp(60(11 31))");
        var candidates = new StandardMccsProtocol().CandidatesFor(caps);
        Assert.Contains(candidates, c => c.Code == 0x31 && c.InputId == "input-31");
    }

    [Fact]
    public void Registry_ids_are_unique_and_lowercase()
    {
        var ids = ProtocolRegistry.All.Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Equal(id.ToLowerInvariant(), id));
    }
}
