import Foundation
@testable import MonitorSwitcherCore

/// A transport that records what it would have sent.
final class FakeTransport: DdcTransport {
    private let transportId: String
    private let raw: Bool
    private let order: Int
    var sent: [(endpoint: DisplayEndpoint, frame: SetVcpFrame)] = []
    var endpoints: [DisplayEndpoint] = []
    var failNext: [Error] = []
    var readable = false
    var answers = true
    var reading = VcpReading(current: 50, maximum: 100)

    init(_ id: String, raw: Bool, priority: Int = 10) {
        transportId = id
        self.raw = raw
        order = priority
        super.init()
    }

    override var id: String { transportId }
    override var displayName: String { "Fake \(transportId)" }
    override var supportsRawI2C: Bool { raw }
    override var priority: Int { order }
    override var supportsReads: Bool { readable }

    override func probe() -> TransportStatus { .available("fake") }
    override func enumerate() throws -> [DisplayEndpoint] { endpoints }

    @discardableResult
    func add(_ identity: MonitorIdentity, osDisplayName: String? = nil) -> DisplayEndpoint {
        let endpoint = DisplayEndpoint(transport: self, identity: identity, connectorId: "\(transportId)-\(endpoints.count)", native: nil)
        endpoint.osDisplayName = osDisplayName
        endpoints.append(endpoint)
        return endpoint
    }

    override func deliverValidated(_ endpoint: DisplayEndpoint, _ frame: SetVcpFrame) throws {
        if !failNext.isEmpty {
            throw failNext.removeFirst()
        }
        sent.append((endpoint, frame))
    }

    override func readVcp(_ endpoint: DisplayEndpoint, code: UInt8) -> VcpReading? {
        readable && answers ? reading : nil
    }
}

enum TestData {
    static let lg32un880 = MonitorIdentity(manufacturerId: "GSM", productCode: 0x774F, serialNumber: 384592, modelName: "LG HDR 4K")
    static let dell = MonitorIdentity(manufacturerId: "DEL", productCode: 0xA0F1, serialNumber: 1234, modelName: "DELL U2720Q")

    /// Capabilities string exactly as the 32UN880 returns it (note the missing "model(").
    static let lg32un880Capabilities =
        "(prot(monitor)type(lcd)UN880cmds(01 02 03 0C E3 F3)vcp(02 04 05 08 10 12 14(05 08 0B ) 16 18 1A 52 60( 11 12 0F 00) AC AE B2 B6 C0 C6 C8 C9 D6(01 04) DF 62 8D F4 F5(00 01 02) F6(00 01 02) 4D 4E 4F 15(01 06 11 13 14 15 18 19 28 29 48) F7(00 01 02 03) F8(00 01) F9 E4 E5 E6 E7 E8 E9 EA EB EF FD(00 01) FE(00 01 02) FF)mccs_ver(2.1)mswhql(1))"

    static func builtInCatalog() -> ProfileCatalog { ProfileCatalog.load(userProfilesDirectory: nil, log: NullLog.instance) }

    static func profile(_ id: String, _ manufacturer: String, protocolId: String = "mccs-standard", productCodes: [String]? = nil,
                        capabilitiesModel: String? = nil, modelNames: [String]? = nil, origin: ProfileOrigin = .builtIn,
                        quirks: ProfileQuirks? = nil, inputs: [(String, String, Bool)] = [("hdmi1", "0x11", false)]) -> MonitorProfile {
        var p = MonitorProfile()
        p.id = id
        p.name = id
        p.match = ProfileMatch(manufacturer: manufacturer, productCodes: productCodes, capabilitiesModel: capabilitiesModel, modelNames: modelNames)
        p.protocolId = protocolId
        p.inputs = inputs.map { ProfileInput(id: $0.0, name: $0.0.uppercased(), code: $0.1, verified: $0.2) }
        p.quirks = quirks
        p.origin = origin
        return p
    }

    static func service(_ transports: [DdcTransport], catalog: ProfileCatalog? = nil) -> SwitchService {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("ms-test-\(UUID().uuidString)")
        let store = JsonFileStore<AppState>(path: dir.appendingPathComponent("state.json"), lock: InProcessLock(), log: NullLog.instance,
                                            makeDefault: { AppState() })
        return SwitchService(transports: transports, catalog: catalog ?? builtInCatalog(), stateStore: store, busLock: InProcessLock(), log: NullLog.instance)
    }

    /// Builds a minimal valid EDID base block.
    static func edid(manufacturerRaw: UInt16, product: UInt16, serial: UInt32, name: String?, serialText: String? = nil) -> [UInt8] {
        var e = [UInt8](repeating: 0, count: 128)
        let header: [UInt8] = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00]
        e.replaceSubrange(0..<8, with: header)
        e[8] = UInt8(manufacturerRaw >> 8)
        e[9] = UInt8(manufacturerRaw & 0xFF)
        e[10] = UInt8(product & 0xFF)
        e[11] = UInt8(product >> 8)
        e[12] = UInt8(serial & 0xFF)
        e[13] = UInt8((serial >> 8) & 0xFF)
        e[14] = UInt8((serial >> 16) & 0xFF)
        e[15] = UInt8((serial >> 24) & 0xFF)
        e[16] = 4
        e[17] = UInt8(2023 - 1990)
        e[54] = 0x01 // first descriptor is a detailed timing

        func descriptor(_ offset: Int, _ tag: UInt8, _ text: String) {
            e[offset + 3] = tag
            let bytes = Array((text.count < 13 ? text + "\n" : String(text.prefix(13))).utf8)
            for i in 0..<13 {
                e[offset + 5 + i] = i < bytes.count ? bytes[i] : 0x20
            }
        }
        if let name { descriptor(72, 0xFC, name) }
        if let serialText { descriptor(90, 0xFF, serialText) }

        var sum: UInt8 = 0
        for i in 0..<127 { sum = sum &+ e[i] }
        e[127] = 0 &- sum
        return e
    }
}
