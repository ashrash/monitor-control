import Foundation
import XCTest
@testable import MonitorSwitcherCore

final class ControlTests: XCTestCase {
    func testParsesAbsoluteAndRelativeChanges() {
        XCTAssertEqual(ControlChange.parse("70"), .set(70))
        XCTAssertEqual(ControlChange.parse("70%"), .set(70))
        XCTAssertEqual(ControlChange.parse("+10"), .adjust(10))
        XCTAssertEqual(ControlChange.parse("-5"), .adjust(-5))
        XCTAssertEqual(ControlChange.parse("0"), .set(0))
        for bad in ["", "150", "-101", "abc", "10.5", "%"] {
            XCTAssertNil(ControlChange.parse(bad), bad)
        }
    }

    func testAppliesAndClampsChanges() {
        XCTAssertEqual(ControlChange.adjust(10).apply(to: 95), 100)
        XCTAssertEqual(ControlChange.adjust(-10).apply(to: 5), 0)
        XCTAssertNil(ControlChange.adjust(10).apply(to: nil))
        XCTAssertEqual(ControlChange.set(40).apply(to: nil), 40)
    }

    func testScalesToTheMonitorsRange() {
        XCTAssertEqual(ControlMath.percent(VcpReading(current: 18, maximum: 100)), 18)
        XCTAssertEqual(ControlMath.percent(VcpReading(current: 128, maximum: 255)), 50)
        XCTAssertEqual(ControlMath.percent(VcpReading(current: 40, maximum: 0)), 40)
        XCTAssertEqual(ControlMath.value(percent: 50, maximum: 255), 128)
        XCTAssertEqual(ControlMath.value(percent: 70, maximum: 100), 70)
        XCTAssertEqual(ControlMath.value(percent: 120, maximum: 100), 100)
    }

    func testGuardAllowsOnlyBrightnessAndVolume() throws {
        try DdcGuard.ensureControlFrame(SetVcpFrame(sourceAddress: 0x51, vcpCode: 0x10, value: 50))
        try DdcGuard.ensureControlFrame(SetVcpFrame(sourceAddress: 0x51, vcpCode: 0x62, value: 50))
        for frame in [SetVcpFrame(sourceAddress: 0x51, vcpCode: 0x60, value: 0x11),
                      SetVcpFrame(sourceAddress: 0x50, vcpCode: 0x10, value: 50),
                      SetVcpFrame(sourceAddress: 0x51, vcpCode: 0xF4, value: 0x91)] {
            XCTAssertThrowsError(try DdcGuard.ensureControlFrame(frame)) { error in
                XCTAssertEqual((error as? DdcError)?.kind, .safetyViolation)
            }
        }
    }

    private func lgMonitor(_ transport: FakeTransport, capabilities: String? = nil) throws -> (SwitchService, ResolvedMonitor) {
        transport.add(TestData.lg32un880)
        let service = TestData.service([transport])
        var monitor = try XCTUnwrap(service.discoverMonitors(readCapabilities: false).first)
        if let capabilities {
            monitor = ResolvedMonitor(identity: monitor.identity, endpoints: monitor.endpoints, profile: monitor.profile,
                                      quality: monitor.quality, capabilities: CapabilitiesParser.parse(capabilities))
        }
        return (service, monitor)
    }

    func testSetsAnAbsoluteLevelScaledToTheMaximum() throws {
        let transport = FakeTransport("fake", raw: true)
        transport.readable = true
        transport.reading = VcpReading(current: 10, maximum: 200)
        let (service, monitor) = try lgMonitor(transport)

        let outcome = try service.setControl(monitor, .brightness, .set(70))

        XCTAssertEqual(outcome.percent, 70)
        XCTAssertEqual(transport.sent.map(\.frame), [SetVcpFrame(sourceAddress: 0x51, vcpCode: 0x10, value: 140)])
        XCTAssertEqual(service.state.load().monitors[monitor.stableId]?.levels["Brightness"], 70)
    }

    func testStepsFromTheCurrentValue() throws {
        let transport = FakeTransport("fake", raw: true)
        transport.readable = true
        let (service, monitor) = try lgMonitor(transport)

        XCTAssertEqual(try service.setControl(monitor, .volume, .adjust(-10)).percent, 40)
        XCTAssertEqual(transport.sent.last?.frame, SetVcpFrame(sourceAddress: 0x51, vcpCode: 0x62, value: 40))
    }

    func testStepWithoutAReadingUsesTheLastValueSet() throws {
        let transport = FakeTransport("fake", raw: true)
        let (service, monitor) = try lgMonitor(transport)

        XCTAssertThrowsError(try service.setControl(monitor, .brightness, .adjust(10))) { error in
            XCTAssertEqual((error as? DdcError)?.kind, .monitorNotResponding)
        }
        XCTAssertTrue(transport.sent.isEmpty)

        _ = try service.setControl(monitor, .brightness, .set(30))
        XCTAssertEqual(try service.setControl(monitor, .brightness, .adjust(10)).percent, 40)
    }

    func testRefusesAControlTheMonitorDoesNotList() throws {
        let transport = FakeTransport("fake", raw: true)
        let (service, monitor) = try lgMonitor(transport, capabilities: "(vcp(10 60(11 12)))")

        XCTAssertThrowsError(try service.setControl(monitor, .volume, .set(10)))
        XCTAssertTrue(transport.sent.isEmpty)
        XCTAssertNoThrow(try service.setControl(monitor, .brightness, .set(10)))
    }
}

final class PeerWireTests: XCTestCase {
    struct Vectors: Decodable {
        struct Message: Decodable { let json: String; let mac: String; let datagram: String }
        let pairingCode: String
        let normalizedCode: String
        let keyHex: String
        let invalidCodes: [String]
        let equivalentCodes: [String]
        let request: Message
        let reply: Message
    }

    private func vectors() throws -> Vectors {
        let url = try XCTUnwrap(Bundle.module.url(forResource: "peer-vectors", withExtension: "json", subdirectory: "spec"),
                                "Run scripts/sync-shared.sh first: spec/peer-vectors.json is missing.")
        return try JSONDecoder().decode(Vectors.self, from: Data(contentsOf: url))
    }

    func testDerivesTheSharedKey() throws {
        let v = try vectors()
        XCTAssertEqual(PairingCode.normalize(v.pairingCode), v.normalizedCode)
        XCTAssertEqual(PeerWire.hex(try XCTUnwrap(PeerKey(pairingCode: v.pairingCode)).bytes), v.keyHex)
        for code in v.equivalentCodes {
            XCTAssertEqual(PeerKey(pairingCode: code), PeerKey(pairingCode: v.pairingCode), code)
        }
        for code in v.invalidCodes {
            XCTAssertNil(PeerKey(pairingCode: code), code)
        }
    }

    func testAcceptsTheGoldenDatagrams() throws {
        let v = try vectors()
        let key = try XCTUnwrap(PeerKey(pairingCode: v.pairingCode))
        XCTAssertEqual(PeerWire.hex(key.mac("request", Data(v.request.json.utf8))), v.request.mac)
        XCTAssertEqual(PeerWire.hex(key.mac("reply", Data(v.reply.json.utf8))), v.reply.mac)

        let request = try XCTUnwrap(PeerWire.decodeRequest(Data(v.request.datagram.utf8), key))
        XCTAssertEqual(request.kind, .switchInput)
        XCTAssertEqual(request.inputId, "usbc")
        XCTAssertEqual(request.monitor?.productCode, 0x774F)
        XCTAssertEqual(request.monitor?.serialNumber, 384592)
        let reply = try XCTUnwrap(PeerWire.decodeReply(Data(v.reply.datagram.utf8), key))
        XCTAssertTrue(reply.ok)
        XCTAssertEqual(reply.inputName, "USB-C")
    }

    func testRejectsWrongKeyLabelOrTampering() throws {
        let v = try vectors()
        let key = try XCTUnwrap(PeerKey(pairingCode: v.pairingCode))
        let other = try XCTUnwrap(PeerKey(pairingCode: PairingCode.generate()))
        let datagram = Data(v.request.datagram.utf8)

        XCTAssertNil(PeerWire.decodeRequest(datagram, other))
        XCTAssertNil(PeerWire.decodeReply(datagram, key), "a request must not be accepted as a reply")
        XCTAssertNil(PeerWire.decodeRequest(Data(v.request.datagram.replacingOccurrences(of: "usbc", with: "hdmi").utf8), key))
        XCTAssertNil(PeerWire.decodeRequest(Data("MSW1 short".utf8), key))
    }

    func testRoundTripsOwnMessages() throws {
        let key = try XCTUnwrap(PeerKey(pairingCode: PairingCode.generate()))
        let request = PeerRequest(machine: "m", from: "Mac", kind: .control, control: "Volume", adjustPercent: -10)
        XCTAssertEqual(PeerWire.decodeRequest(try PeerWire.encode(request, key), key), request)
    }

    func testGeneratedCodesAreValidAndDifferent() {
        let a = PairingCode.generate(), b = PairingCode.generate()
        XCTAssertNotEqual(a, b)
        XCTAssertEqual(PairingCode.normalize(a)?.count, PairingCode.length)
        XCTAssertEqual(a.count, PairingCode.length + PairingCode.length / 4 - 1)
    }

    func testReplayGuardRefusesStaleRequestsAndRemembersReplies() {
        let guardian = PeerReplayGuard()
        let now = Date()
        var request = PeerRequest(ts: Int64(now.timeIntervalSince1970) - 300, machine: "m", from: "PC", kind: .ping)
        XCTAssertEqual(guardian.clockSkew(request, now: now), 300)
        request.ts = Int64(now.timeIntervalSince1970) - 30
        XCTAssertNil(guardian.clockSkew(request, now: now))

        var cached: Data?
        XCTAssertFalse(guardian.isDuplicate(request, cached: &cached))
        guardian.remember(request, reply: Data([1, 2, 3]), now: now)
        XCTAssertTrue(guardian.isDuplicate(request, cached: &cached))
        XCTAssertEqual(cached, Data([1, 2, 3]))
    }
}

final class PeerMonitorMatchingTests: XCTestCase {
    func testMatchesTheSameMonitorUnderAnotherProductCode() throws {
        // The PC sees the 32UN880 as 0x774F, the Mac as 0x7750; the serial is the same.
        var viaMac = TestData.lg32un880
        viaMac.productCode = 0x7750
        let transport = FakeTransport("fake", raw: true)
        transport.add(TestData.dell)
        transport.add(viaMac)
        let monitors = try TestData.service([transport]).discoverMonitors(readCapabilities: false)

        let ref = PeerMonitorRef(manufacturerId: "GSM", productCode: 0x774F, serialNumber: 384592, profileId: "lg-32un880")
        XCTAssertEqual(ref.find(in: monitors)?.stableId, viaMac.stableId)
    }

    func testFallsBackToTheProfileOnlyWhenUnambiguous() throws {
        let noSerial = MonitorIdentity(manufacturerId: "GSM", productCode: 0x774F, serialNumber: 0, modelName: "LG HDR 4K")
        let transport = FakeTransport("fake", raw: true)
        transport.add(noSerial)
        let monitors = try TestData.service([transport]).discoverMonitors(readCapabilities: false)

        XCTAssertNotNil(PeerMonitorRef(manufacturerId: "GSM", productCode: 0x7750, serialNumber: 0, profileId: "lg-32un880").find(in: monitors))
        XCTAssertNil(PeerMonitorRef(manufacturerId: "GSM", productCode: 0x7750, serialNumber: 0, profileId: nil).find(in: monitors))
        XCTAssertNil(PeerMonitorRef(manufacturerId: "GSM", productCode: 0x774F, serialNumber: 999, profileId: nil).find(in: monitors))
    }
}

final class MonitorActionsTests: XCTestCase {
    final class FakeChannel: PeerChannel {
        var requests: [PeerRequest] = []
        var answer: (PeerRequest) -> PeerReply = { PeerReply(id: $0.id, from: "PC", ok: true, message: "Done.", percent: 30) }
        func send(_ request: PeerRequest) throws -> PeerReply {
            requests.append(request)
            return answer(request)
        }
    }

    private func setUp(answers: Bool, paired: Bool) throws -> (MonitorActions, FakeTransport, FakeChannel, ResolvedMonitor) {
        let transport = FakeTransport("fake", raw: true)
        transport.readable = true
        transport.answers = answers
        transport.add(TestData.lg32un880)
        let service = TestData.service([transport])
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("ms-actions-\(UUID().uuidString)")
        let settings = JsonFileStore<AppSettings>(path: root.appendingPathComponent("settings.json"), lock: InProcessLock(), log: NullLog.instance,
                                                  makeDefault: { AppSettings() })
        let services = AppServices(paths: AppPaths(root: root), log: NullLog.instance, transports: [transport], settings: settings,
                                   state: service.state, switcher: service)
        let channel = FakeChannel()
        let actions = MonitorActions(services: services, machine: PeerMachine(id: "mac", name: "Mac"), peer: paired ? channel : nil)
        let monitor = try XCTUnwrap(service.discoverMonitors(readCapabilities: false).first)
        return (actions, transport, channel, monitor)
    }

    private func macSettings(_ monitor: ResolvedMonitor) -> MonitorSettings {
        let ms = MonitorSettings(monitorId: monitor.stableId)
        ms.localInputId = "usbc"
        ms.toggleInputs = ["usbc", "hdmi2"]
        return ms
    }

    func testActsLocallyWhileTheMonitorAnswers() throws {
        let (actions, transport, channel, monitor) = try setUp(answers: true, paired: true)

        let result = try actions.toggle(monitor, settings: macSettings(monitor))

        XCTAssertNil(result.viaPeer)
        XCTAssertEqual(result.inputId, "hdmi2")
        XCTAssertEqual(transport.sent.map(\.frame), [SetVcpFrame(sourceAddress: 0x50, vcpCode: 0xF4, value: 0x91)])
        XCTAssertTrue(channel.requests.isEmpty)
    }

    func testToggleAsksThePeerToComeBackWhenTheMonitorIgnoresUs() throws {
        let (actions, transport, channel, monitor) = try setUp(answers: false, paired: true)

        let result = try actions.toggle(monitor, settings: macSettings(monitor))

        XCTAssertEqual(result.viaPeer, "PC")
        XCTAssertEqual(result.inputId, "usbc")
        XCTAssertTrue(transport.sent.isEmpty, "nothing is sent to a monitor that ignores us")
        let request = try XCTUnwrap(channel.requests.first)
        XCTAssertEqual(request.kind, .switchInput)
        XCTAssertEqual(request.inputId, "usbc")
        XCTAssertEqual(request.monitor?.serialNumber, 384592)
        XCTAssertEqual(actions.services.state.load().monitors[monitor.stableId]?.lastInputId, "usbc")
    }

    func testBrightnessIsRelayedToo() throws {
        let (actions, _, channel, monitor) = try setUp(answers: false, paired: true)

        let result = try actions.setControl(monitor, .brightness, .adjust(10))

        XCTAssertEqual(result.percent, 30)
        XCTAssertEqual(channel.requests.first?.control, "Brightness")
        XCTAssertEqual(channel.requests.first?.adjustPercent, 10)
    }

    func testUnpairedComputerExplainsInsteadOfRelaying() throws {
        let (actions, transport, _, monitor) = try setUp(answers: false, paired: false)

        XCTAssertThrowsError(try actions.switchInput(monitor, inputId: "usbc", settings: macSettings(monitor))) { error in
            XCTAssertEqual((error as? DdcError)?.kind, .monitorOnOtherInput)
        }
        XCTAssertTrue(transport.sent.isEmpty)
    }

    func testARefusalFromThePeerIsAnError() throws {
        let (actions, _, channel, monitor) = try setUp(answers: false, paired: true)
        channel.answer = { PeerReply(id: $0.id, from: "PC", ok: false, message: "Not connected.") }

        XCTAssertThrowsError(try actions.switchInput(monitor, inputId: "usbc", settings: nil)) { error in
            XCTAssertEqual((error as? DdcError)?.kind, .peerUnavailable)
        }
    }

    func testHandlesARequestFromThePeerWithItsOwnProfile() throws {
        let (actions, transport, channel, monitor) = try setUp(answers: true, paired: true)
        var ref = PeerMonitorRef(monitor)
        ref.productCode = 0x7750

        let reply = actions.handle(PeerRequest(machine: "pc", from: "PC", kind: .switchInput, monitor: ref, inputId: "hdmi2"), settings: AppSettings())
        XCTAssertTrue(reply.ok, reply.message)
        XCTAssertEqual(reply.inputName, "HDMI 2")
        XCTAssertEqual(transport.sent.last?.frame, SetVcpFrame(sourceAddress: 0x50, vcpCode: 0xF4, value: 0x91))

        let bad = actions.handle(PeerRequest(machine: "pc", from: "PC", kind: .switchInput, monitor: ref, inputId: "vga1"), settings: AppSettings())
        XCTAssertFalse(bad.ok)
        XCTAssertTrue(channel.requests.isEmpty, "a handled request is never relayed again")
    }
}
