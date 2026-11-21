import XCTest
@testable import MonitorSwitcherCore

final class ProfileMatchingTests: XCTestCase {
    func testLg32un880MatchesItsBuiltInProfileByProductCode() throws {
        let match = TestData.builtInCatalog().resolve(TestData.lg32un880, nil)
        XCTAssertEqual(match.profile.id, "lg-32un880", "Run scripts/sync-shared.sh so the bundled profiles exist.")
        XCTAssertEqual(match.quality, .productCode)
        XCTAssertEqual(match.profile.protocolId, LgAlternativeProtocol.protocolId)
        XCTAssertTrue(match.profile.effectiveQuirks.ignoresCommandsWhenInactive)
        XCTAssertEqual(match.profile.findInput("usbc")?.code, "0xD1")
    }

    func testMacConnectionProductCodeMatchesByProductCode() {
        // Over USB-C/DP to a Mac the same 32UN880 reports 0x7750 instead of 0x774F.
        var viaMac = TestData.lg32un880
        viaMac.productCode = 0x7750
        let match = TestData.builtInCatalog().resolve(viaMac, nil)
        XCTAssertEqual(match.profile.id, "lg-32un880")
        XCTAssertEqual(match.quality, .productCode)
    }

    func testSiblingModelMatchesByCapabilitiesModel() {
        var sibling = TestData.lg32un880
        sibling.productCode = 0x7700
        let match = TestData.builtInCatalog().resolve(sibling, CapabilitiesParser.parse(TestData.lg32un880Capabilities))
        XCTAssertEqual(match.profile.id, "lg-32un880")
        XCTAssertEqual(match.quality, .capabilitiesModel)
    }

    func testGenericLgEdidNameAloneDoesNotMatch() {
        let otherLg = MonitorIdentity(manufacturerId: "GSM", productCode: 0x5B08, serialNumber: 1, modelName: "LG HDR 4K")
        let match = TestData.builtInCatalog().resolve(otherLg, nil)
        XCTAssertEqual(match.profile.origin, .fallback)
        XCTAssertEqual(match.quality, .fallback)
    }

    func testUnknownMonitorGetsStandardFallbackFromCapabilities() {
        let match = TestData.builtInCatalog().resolve(TestData.dell, CapabilitiesParser.parse("(model(U2720Q)vcp(60(0F 11 1B)))"))
        XCTAssertEqual(match.profile.origin, .fallback)
        XCTAssertEqual(match.profile.protocolId, StandardMccsProtocol.protocolId)
        XCTAssertEqual(match.profile.inputs.map(\.id).sorted(), ["dp1", "hdmi1", "usbc"])
        XCTAssertTrue(match.profile.inputs.allSatisfy { !$0.verified })
        XCTAssertEqual(ProfileValidator.validate(match.profile), [])
    }

    func testManufacturerMustMatch() {
        let profile = TestData.profile("x", "GSM", productCodes: ["0xA0F1"])
        XCTAssertEqual(ProfileMatcher.evaluate(profile, TestData.dell, nil), MatchQuality.none)
    }

    func testProductCodeBeatsModelName() {
        let byName = TestData.profile("by-name", "DEL", modelNames: ["DELL U2720Q"])
        let byCode = TestData.profile("by-code", "DEL", productCodes: ["0xA0F1"])
        let best = ProfileMatcher.findBest([byName, byCode], TestData.dell, nil)
        XCTAssertEqual(best?.profile.id, "by-code")
        XCTAssertEqual(best?.quality, .productCode)
    }

    func testUserProfileWinsATie() {
        let builtIn = TestData.profile("a-builtin", "DEL", productCodes: ["0xA0F1"])
        let user = TestData.profile("z-user", "DEL", productCodes: ["0xA0F1"], origin: .user)
        XCTAssertEqual(ProfileMatcher.findBest([builtIn, user], TestData.dell, nil)?.profile.id, "z-user")
    }

    func testForcedProfileOverridesMatching() {
        let catalog = ProfileCatalog(profiles: [TestData.profile("forced", "XXX", productCodes: ["0x0001"])], errors: [])
        XCTAssertEqual(catalog.resolve(TestData.dell, nil, forcedProfileId: "forced").profile.id, "forced")
    }
}

final class ProfileFileTests: XCTestCase {
    func testEveryBundledProfileLoadsAndIsNamedAfterItsId() throws {
        let catalog = TestData.builtInCatalog()
        XCTAssertTrue(catalog.errors.isEmpty, catalog.errors.map { "\($0.source): \($0.message)" }.joined(separator: "\n"))
        XCTAssertTrue(catalog.profiles.contains { $0.id == "lg-32un880" })
        XCTAssertEqual(Set(catalog.profiles.map(\.id)).count, catalog.profiles.count)
        for profile in catalog.profiles {
            XCTAssertEqual(profile.sourcePath, "\(profile.id).json")
            XCTAssertEqual(profile.schema, "../profile.schema.json")
        }
    }

    func testUserProfilesFolderIsLoadedAndBadFilesAreReported() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("ms-profiles-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        try """
        { "id": "dell-u2720q", "name": "Dell U2720Q", "match": { "manufacturer": "DEL", "productCodes": ["0xA0F1"] },
          "protocol": "mccs-standard", "inputs": [ { "id": "usbc", "name": "USB-C", "code": "0x1B" } ] }
        """.write(to: dir.appendingPathComponent("dell-u2720q.json"), atomically: true, encoding: .utf8)
        try "{ not json".write(to: dir.appendingPathComponent("broken.json"), atomically: true, encoding: .utf8)

        let catalog = ProfileCatalog.load(userProfilesDirectory: dir, log: NullLog.instance)
        XCTAssertTrue(catalog.profiles.contains { $0.id == "dell-u2720q" && $0.origin == .user })
        XCTAssertEqual(catalog.errors.count, 1)
    }
}

final class ProfileValidationTests: XCTestCase {
    private let valid = """
    { "id": "acme-x1", "name": "Acme X1", "match": { "manufacturer": "ACM", "productCodes": ["0x0001"] },
      "protocol": "mccs-standard", "inputs": [ { "id": "hdmi1", "name": "HDMI 1", "code": "0x11" } ] }
    """

    private func errors(_ json: String) -> [String] {
        do {
            _ = try ProfileCatalog.parse(json, origin: .user, source: "test.json")
            return []
        } catch let error as ProfileFormatError {
            return error.errors
        } catch {
            return ["\(error)"]
        }
    }

    func testValidProfileParses() {
        XCTAssertEqual(errors(valid), [])
    }

    func testInvalidFieldsAreReported() {
        let cases: [(String, String, String)] = [
            ("\"id\": \"acme-x1\"", "\"id\": \"Acme X1\"", "id"),
            ("\"manufacturer\": \"ACM\"", "\"manufacturer\": \"acme\"", "manufacturer"),
            ("\"protocol\": \"mccs-standard\"", "\"protocol\": \"made-up\"", "protocol"),
            ("\"code\": \"0x11\"", "\"code\": \"11\"", "code"),
            ("\"code\": \"0x11\"", "\"code\": \"0x10000\"", "code"),
            ("\"productCodes\": [\"0x0001\"]", "\"productCodes\": [\"774F\"]", "productCodes"),
        ]
        for (find, replace, word) in cases {
            let problems = errors(valid.replacingOccurrences(of: find, with: replace))
            XCTAssertTrue(problems.contains { $0.localizedCaseInsensitiveContains(word) }, "\(replace): \(problems)")
        }
    }

    func testAProfileMayNotClaimEveryMonitorOfAManufacturer() {
        let problems = errors(valid.replacingOccurrences(of: ", \"productCodes\": [\"0x0001\"]", with: ""))
        XCTAssertTrue(problems.contains { $0.contains("productCodes, capabilitiesModel or modelNames") })
    }

    func testDuplicateInputIdsAreRejected() {
        let json = valid.replacingOccurrences(
            of: "[ { \"id\": \"hdmi1\", \"name\": \"HDMI 1\", \"code\": \"0x11\" } ]",
            with: "[ { \"id\": \"hdmi1\", \"name\": \"HDMI 1\", \"code\": \"0x11\" }, { \"id\": \"hdmi1\", \"name\": \"Again\", \"code\": \"0x12\" } ]")
        XCTAssertTrue(errors(json).contains { $0.contains("more than once") })
    }

    func testUnknownPropertiesAreRejectedToCatchTypos() {
        XCTAssertFalse(errors(valid.replacingOccurrences(of: "\"productCodes\"", with: "\"productCode\"")).isEmpty)
    }

    func testProfilesRoundTrip() throws {
        let profile = try ProfileCatalog.parse(valid, origin: .user, source: "x")
        let again = try ProfileCatalog.parse(ProfileCatalog.serialize(profile), origin: .user, source: "y")
        XCTAssertEqual(again.inputs.map(\.code), profile.inputs.map(\.code))
        XCTAssertEqual(again.match.productCodes, profile.match.productCodes)
    }
}

final class ProtocolTests: XCTestCase {
    func testLgAlternativeBuildsSource0x50Vcp0xF4() {
        let steps = LgAlternativeProtocol().buildSwitch(code: 0xD1)
        XCTAssertEqual(steps.count, 1)
        XCTAssertEqual(steps[0].frame, SetVcpFrame(sourceAddress: 0x50, vcpCode: 0xF4, value: 0xD1))
    }

    func testStandardBuildsSource0x51Vcp0x60() {
        XCTAssertEqual(StandardMccsProtocol().buildSwitch(code: 0x12)[0].frame, SetVcpFrame(sourceAddress: 0x51, vcpCode: 0x60, value: 0x12))
    }

    func testOnlyVendorProtocolsRequireRawI2C() {
        XCTAssertTrue(LgAlternativeProtocol().requiresRawI2C)
        XCTAssertFalse(StandardMccsProtocol().requiresRawI2C)
    }

    func testLgMonitorsTryTheLgProtocolFirst() {
        let ranked = ProtocolRegistry.ranked(for: TestData.lg32un880, capabilities: CapabilitiesParser.parse(TestData.lg32un880Capabilities))
        XCTAssertEqual(ranked.map { $0.id }, [LgAlternativeProtocol.protocolId, StandardMccsProtocol.protocolId])
        XCTAssertEqual(ProtocolRegistry.ranked(for: TestData.dell, capabilities: nil).map { $0.id }, [StandardMccsProtocol.protocolId])
    }

    func testStandardCandidatesFollowCapabilities() {
        let caps = CapabilitiesParser.parse(TestData.lg32un880Capabilities)
        XCTAssertEqual(StandardMccsProtocol().candidates(for: caps).map(\.inputId).sorted(), ["dp1", "hdmi1", "hdmi2"])
        let extra = StandardMccsProtocol().candidates(for: CapabilitiesParser.parse("vcp(60(11 31))"))
        XCTAssertTrue(extra.contains { $0.code == 0x31 && $0.inputId == "input-31" })
    }
}
