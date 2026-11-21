import XCTest
@testable import MonitorSwitcherCore

final class SwitchServiceTests: XCTestCase {
    func testSwitchingSendsOneLgFrameToExactlyOneEndpoint() throws {
        let nv = FakeTransport("raw", raw: true)
        nv.add(TestData.lg32un880, osDisplayName: "LG")
        nv.add(TestData.dell, osDisplayName: "DELL")
        let service = TestData.service([nv])
        let lg = try XCTUnwrap(try service.discoverMonitors(readCapabilities: false).first { $0.identity.manufacturerId == "GSM" })

        let outcome = try service.switchTo(lg, inputId: "usbc")

        XCTAssertEqual(nv.sent.count, 1)
        XCTAssertTrue(nv.sent[0].endpoint === lg.endpoints[0])
        XCTAssertEqual(nv.sent[0].frame, SetVcpFrame(sourceAddress: 0x50, vcpCode: 0xF4, value: 0xD1))
        XCTAssertEqual(outcome.inputId, "usbc")
    }

    func testInputsOutsideTheProfileAreRefused() throws {
        let nv = FakeTransport("raw", raw: true)
        nv.add(TestData.lg32un880)
        let service = TestData.service([nv])
        let lg = try XCTUnwrap(try service.discoverMonitors(readCapabilities: false).first)
        XCTAssertThrowsError(try service.switchTo(lg, inputId: "hdmi7")) { error in
            XCTAssertEqual((error as? DdcError)?.kind, .inputNotInProfile)
        }
        XCTAssertTrue(nv.sent.isEmpty)
    }

    func testLgProtocolIsNeverRoutedThroughAVcpOnlyTransport() throws {
        let vcp = FakeTransport("vcp", raw: false, priority: 50)
        vcp.add(TestData.lg32un880)
        let service = TestData.service([vcp])
        let lg = try XCTUnwrap(try service.discoverMonitors(readCapabilities: false).first)
        XCTAssertThrowsError(try service.switchTo(lg, inputId: "usbc")) { error in
            XCTAssertEqual((error as? DdcError)?.kind, .unsupportedByTransport)
        }
        XCTAssertTrue(vcp.sent.isEmpty)
    }

    func testEndpointWithoutSerialJoinsTheSameMonitor() throws {
        let a = FakeTransport("a", raw: true)
        let b = FakeTransport("b", raw: false, priority: 50)
        a.add(TestData.lg32un880, osDisplayName: "LG")
        var noSerial = TestData.lg32un880
        noSerial.serialNumber = 0
        b.add(noSerial, osDisplayName: "LG")
        let monitors = try TestData.service([a, b]).discoverMonitors(readCapabilities: false)
        XCTAssertEqual(monitors.count, 1)
        XCTAssertEqual(monitors[0].stableId, "GSM-774F-0005DE50")
        XCTAssertEqual(monitors[0].endpoints.count, 2)
    }

    func testIdenticalModelsWithDifferentSerialsStaySeparate() throws {
        let nv = FakeTransport("raw", raw: true)
        nv.add(TestData.lg32un880, osDisplayName: "one")
        var second = TestData.lg32un880
        second.serialNumber = 999
        nv.add(second, osDisplayName: "two")
        XCTAssertEqual(try TestData.service([nv]).discoverMonitors(readCapabilities: false).count, 2)
    }

    func testTransientFailuresAreRetried() throws {
        let nv = FakeTransport("raw", raw: true)
        nv.add(TestData.lg32un880)
        nv.failNext = [DdcError(.transportFailure, "flaky")]
        let service = TestData.service([nv])
        let lg = try XCTUnwrap(try service.discoverMonitors(readCapabilities: false).first)
        let outcome = try service.switchTo(lg, inputId: "usbc")
        XCTAssertEqual(outcome.attempts, 2)
        XCTAssertEqual(nv.sent.count, 1)
    }

    func testSafetyViolationsAreNeverRetried() throws {
        let nv = FakeTransport("raw", raw: true)
        nv.add(TestData.lg32un880)
        nv.failNext = [DdcError.safety("blocked")]
        let service = TestData.service([nv])
        let lg = try XCTUnwrap(try service.discoverMonitors(readCapabilities: false).first)
        XCTAssertThrowsError(try service.switchTo(lg, inputId: "usbc"))
        XCTAssertTrue(nv.sent.isEmpty)
    }

    func testWizardCanOnlySendTheProtocolsOwnCandidates() throws {
        let nv = FakeTransport("raw", raw: true)
        nv.add(TestData.lg32un880)
        let service = TestData.service([nv])
        let lg = try XCTUnwrap(try service.discoverMonitors(readCapabilities: false).first)
        let lgProtocol = LgAlternativeProtocol()
        XCTAssertThrowsError(try service.sendCandidate(lg, using: lgProtocol, candidate: InputCandidate("usbc", "USB-C", 0x42)))
        _ = try service.sendCandidate(lg, using: lgProtocol, candidate: try XCTUnwrap(lgProtocol.candidates.first { $0.inputId == "usbc" }))
        XCTAssertEqual(nv.sent.count, 1)
    }

    func testLastSentInputIsRemembered() throws {
        let nv = FakeTransport("raw", raw: true)
        nv.add(TestData.lg32un880)
        let service = TestData.service([nv])
        let lg = try XCTUnwrap(try service.discoverMonitors(readCapabilities: false).first)
        _ = try service.switchTo(lg, inputId: "usbc")
        let state = try XCTUnwrap(service.state.load().monitors[lg.stableId])
        XCTAssertEqual(state.lastInputId, "usbc")
        XCTAssertEqual(state.identity, TestData.lg32un880)
    }

    func testSwitchingToThisComputersInputExplainsWhyItCannotWorkWhenTheMonitorIsAway() throws {
        let nv = FakeTransport("raw", raw: true)
        nv.readable = true
        nv.answers = false
        nv.add(TestData.lg32un880)
        let service = TestData.service([nv])
        let lg = try XCTUnwrap(try service.discoverMonitors(readCapabilities: false).first)
        let settings = MonitorSettings(monitorId: lg.stableId)
        settings.localInputId = "hdmi2"

        XCTAssertThrowsError(try service.switchTo(lg, inputId: "hdmi2", settings: settings)) { error in
            XCTAssertEqual((error as? DdcError)?.kind, .monitorOnOtherInput)
        }
        XCTAssertTrue(nv.sent.isEmpty)

        nv.answers = true
        _ = try service.switchTo(lg, inputId: "hdmi2", settings: settings)
        XCTAssertEqual(nv.sent.count, 1)
    }

    func testNoTransportIsAClearError() {
        XCTAssertThrowsError(try TestData.service([]).discoverMonitors()) { error in
            XCTAssertEqual((error as? DdcError)?.kind, .noSupportedTransport)
        }
    }
}

final class ToggleAndResolverTests: XCTestCase {
    func testToggleAlternatesUsingLastSentThenLocalInput() {
        let cases: [(String?, String?, String)] = [("hdmi2", nil, "usbc"), ("usbc", nil, "hdmi2"), (nil, "hdmi2", "usbc"), (nil, "usbc", "hdmi2"), (nil, nil, "usbc")]
        for (last, local, expected) in cases {
            XCTAssertEqual(ToggleLogic.nextInput("hdmi2", "usbc", lastInputId: last, localInputId: local), expected)
        }
    }

    func testMonitorThatOnlyObeysTheActiveComputerAlwaysGoesToTheOtherInput() {
        for last in ["usbc", "hdmi2", nil] {
            XCTAssertEqual(ToggleLogic.nextInput("hdmi2", "usbc", lastInputId: last, localInputId: "hdmi2", ignoresCommandsWhenInactive: true), "usbc")
        }
    }

    func testToggleInputsComeFromSettingsThenVerifiedInputs() {
        let profile = TestData.profile("p", "GSM", inputs: [("hdmi1", "0x90", false), ("hdmi2", "0x91", true), ("usbc", "0xD1", true)])
        let settings = MonitorSettings(monitorId: "m")
        settings.toggleInputs = ["hdmi1", "usbc"]
        XCTAssertEqual(ToggleLogic.toggleInputs(profile, settings)?.a, "hdmi1")
        XCTAssertEqual(ToggleLogic.toggleInputs(profile, nil)?.b, "usbc")
        settings.toggleInputs = ["usbc", "usbc"]
        XCTAssertEqual(ToggleLogic.toggleInputs(profile, settings)?.a, "hdmi2")
        let ambiguous = TestData.profile("q", "GSM", inputs: [("hdmi1", "0x90", false), ("hdmi2", "0x91", false), ("usbc", "0xD1", false)])
        XCTAssertNil(ToggleLogic.toggleInputs(ambiguous, nil))
    }

    func testInputsResolveByIdNameLabelOrUniquePrefix() throws {
        let lg = try XCTUnwrap(TestData.builtInCatalog().findById("lg-32un880"))
        let settings = MonitorSettings(monitorId: "m")
        settings.inputLabels = ["usbc": "Mac", "hdmi2": "PC"]
        for (query, expected) in [("usbc", "usbc"), ("USB-C", "usbc"), ("Mac", "usbc"), ("pc", "hdmi2"), ("display", "dp1"), ("us", "usbc")] {
            XCTAssertEqual(try InputResolver.resolve(lg, settings, query).id, expected, query)
        }
        XCTAssertThrowsError(try InputResolver.resolve(lg, nil, "hdmi"))
    }

    private func monitor(_ identity: MonitorIdentity, _ name: String) -> ResolvedMonitor {
        let transport = FakeTransport("t", raw: true)
        return ResolvedMonitor(identity: identity, endpoints: [transport.add(identity)], profile: TestData.profile(name, identity.manufacturerId),
                               quality: .productCode, capabilities: nil)
    }

    func testMonitorResolutionNeverBroadcasts() throws {
        let lg = monitor(TestData.lg32un880, "lg")
        let dell = monitor(TestData.dell, "dell")
        XCTAssertTrue(try MonitorResolver.resolve([lg], AppSettings(), nil) === lg)
        XCTAssertThrowsError(try MonitorResolver.resolve([lg, dell], AppSettings(), nil)) { error in
            XCTAssertEqual((error as? DdcError)?.kind, .ambiguousMonitor)
        }

        let settings = AppSettings()
        settings.defaultMonitorId = dell.stableId
        settings.getOrAddMonitor(lg.stableId).label = "Desk"
        XCTAssertTrue(try MonitorResolver.resolve([lg, dell], settings, nil) === dell)
        XCTAssertTrue(try MonitorResolver.resolve([lg, dell], settings, "1") === lg)
        XCTAssertTrue(try MonitorResolver.resolve([lg, dell], settings, "desk") === lg)
        XCTAssertTrue(try MonitorResolver.resolve([lg, dell], settings, "U2720") === dell)
        XCTAssertThrowsError(try MonitorResolver.resolve([], AppSettings(), nil)) { error in
            XCTAssertEqual((error as? DdcError)?.kind, .monitorNotFound)
        }
    }
}
