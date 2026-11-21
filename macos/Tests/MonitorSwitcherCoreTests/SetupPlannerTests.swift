import XCTest
@testable import MonitorSwitcherCore

final class SetupPlannerTests: XCTestCase {
    private func discover(_ identity: MonitorIdentity, raw: Bool = true, capabilities: String? = nil) throws -> ResolvedMonitor {
        let transport = FakeTransport(raw ? "raw" : "vcp", raw: raw)
        transport.add(identity)
        let service = TestData.service([transport])
        let monitor = try XCTUnwrap(try service.discoverMonitors(readCapabilities: false).first)
        guard let capabilities else { return monitor }
        let caps = CapabilitiesParser.parse(capabilities)
        let match = service.catalog.resolve(identity, caps)
        return ResolvedMonitor(identity: monitor.identity, endpoints: monitor.endpoints, profile: match.profile, quality: match.quality, capabilities: caps)
    }

    func testKnownLgTriesItsProfileCodeFirstAndReturnsWithTheProfileCode() throws {
        let lg = try discover(TestData.lg32un880)
        let attempts = SetupPlanner.plan(lg, localInputId: "hdmi2", targetInputId: "usbc")
        XCTAssertEqual(attempts[0].inputProtocol.id, LgAlternativeProtocol.protocolId)
        XCTAssertEqual(attempts[0].target.code, 0xD1)
        XCTAssertEqual(attempts[0].back?.code, 0x91)
        XCTAssertEqual(attempts.filter { $0.inputProtocol.id == LgAlternativeProtocol.protocolId }.map(\.target.code), [0xD1, 0xD2, 0xD3])
    }

    func testWithLgCapabilitiesTheStandardProtocolAddsNoUsbCGuesses() throws {
        let lg = try discover(TestData.lg32un880, capabilities: TestData.lg32un880Capabilities)
        XCTAssertTrue(SetupPlanner.plan(lg, localInputId: "hdmi2", targetInputId: "usbc").allSatisfy { $0.inputProtocol.id == LgAlternativeProtocol.protocolId })
    }

    func testEveryPlannedCodeComesFromTheProfileOrTheProtocolsCandidates() throws {
        let lg = try discover(TestData.lg32un880)
        for attempt in SetupPlanner.plan(lg, localInputId: "hdmi2", targetInputId: "usbc") {
            var allowed = attempt.inputProtocol.candidates.map(\.code)
            if attempt.inputProtocol.id == lg.profile.protocolId {
                allowed += lg.profile.inputs.map(\.codeValue)
            }
            XCTAssertTrue(allowed.contains(attempt.target.code))
        }
    }

    func testUnknownMonitorUsesCodesItsCapabilitiesList() throws {
        let dell = try discover(TestData.dell, capabilities: "(model(U2720Q)vcp(60(0F 11 1B)))")
        let attempts = SetupPlanner.plan(dell, localInputId: "hdmi1", targetInputId: "usbc")
        XCTAssertEqual(attempts.count, 1)
        XCTAssertEqual(attempts[0].inputProtocol.id, StandardMccsProtocol.protocolId)
        XCTAssertEqual(attempts[0].target.code, 0x1B)
        XCTAssertEqual(attempts[0].back?.code, 0x11)
    }

    func testVendorProtocolsAreSkippedWhenOnlyAVcpTransportReachesTheMonitor() throws {
        let lg = try discover(TestData.lg32un880, raw: false)
        XCTAssertFalse(SetupPlanner.usableProtocols(lg).contains { $0.requiresRawI2C })
    }

    func testPortChoicesForAKnownMonitorAreItsProfileInputs() throws {
        XCTAssertEqual(SetupPlanner.portChoices(try discover(TestData.lg32un880)).map(\.inputId), ["hdmi1", "hdmi2", "dp1", "usbc"])
    }

    func testConfirmingBuiltInCodesNeedsNoUserProfile() throws {
        let lg = try discover(TestData.lg32un880)
        let lgProtocol = LgAlternativeProtocol()
        let confirmed = [ConfirmedInput("usbc", "USB-C", 0xD1, verified: true), ConfirmedInput("hdmi2", "HDMI 2", 0x91, verified: false)]
        XCTAssertFalse(SetupPlanner.needsUserProfile(lg, lgProtocol, confirmed, ignoresCommandsWhenInactive: nil))
        XCTAssertFalse(SetupPlanner.needsUserProfile(lg, lgProtocol, confirmed, ignoresCommandsWhenInactive: true))
        XCTAssertTrue(SetupPlanner.needsUserProfile(lg, lgProtocol, confirmed, ignoresCommandsWhenInactive: false))
        XCTAssertTrue(SetupPlanner.needsUserProfile(lg, lgProtocol, [ConfirmedInput("usbc", "USB-C", 0xD2, verified: true)], ignoresCommandsWhenInactive: nil))
        XCTAssertTrue(SetupPlanner.needsUserProfile(lg, StandardMccsProtocol(), confirmed, ignoresCommandsWhenInactive: nil))
    }

    func testUserProfileForAnUnknownMonitorIsValidAndSpecific() throws {
        let dell = try discover(TestData.dell, capabilities: "(vcp(60(0F 11 1B)))")
        let confirmed = [ConfirmedInput("usbc", "USB-C", 0x1B, verified: true), ConfirmedInput("hdmi1", "HDMI 1", 0x11, verified: true)]
        let profile = SetupPlanner.buildUserProfile(dell, StandardMccsProtocol(), confirmed, ignoresCommandsWhenInactive: false, existingUserProfile: nil)

        XCTAssertEqual(ProfileValidator.validate(profile), [])
        XCTAssertEqual(profile.id, "del-a0f1")
        XCTAssertEqual(profile.match.productCodes ?? [], ["0xA0F1"])
        XCTAssertNil(profile.match.capabilitiesModel)
        XCTAssertEqual(profile.inputs.map(\.id), ["usbc", "hdmi1"])
        XCTAssertTrue(profile.inputs.allSatisfy(\.verified))
        XCTAssertEqual(try ProfileCatalog.parse(ProfileCatalog.serialize(profile), origin: .user, source: "x").protocolId, StandardMccsProtocol.protocolId)
    }

    func testUserProfileBuiltOnAKnownModelKeepsItsOtherInputsAndQuirks() throws {
        let lg = try discover(TestData.lg32un880)
        let profile = SetupPlanner.buildUserProfile(lg, LgAlternativeProtocol(), [ConfirmedInput("usbc", "USB-C", 0xD2, verified: true)],
                                                    ignoresCommandsWhenInactive: nil, existingUserProfile: nil)
        XCTAssertEqual(ProfileValidator.validate(profile), [])
        XCTAssertEqual(profile.findInput("usbc")?.code, "0xD2")
        XCTAssertEqual(profile.findInput("hdmi2")?.code, "0x91")
        XCTAssertTrue(profile.effectiveQuirks.ignoresCommandsWhenInactive)
        XCTAssertEqual(profile.name, lg.profile.name)

        let catalog = ProfileCatalog(profiles: TestData.builtInCatalog().profiles + [profile], errors: [])
        XCTAssertEqual(catalog.resolve(TestData.lg32un880, nil).profile.id, profile.id, "a saved user profile wins over the built-in one")
    }
}
