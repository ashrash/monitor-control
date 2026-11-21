import CryptoKit
import Foundation

// Messages between the two computers that share a monitor. Mirrors `PeerProtocol.cs`; both sides
// must produce and accept exactly the same bytes (see spec/peer-protocol.md).
//
// A datagram is UTF-8 text: "MSW1 <64 hex HMAC> <JSON>". The HMAC-SHA256 covers
// "MSW1 request\n" or "MSW1 reply\n" followed by the JSON bytes exactly as sent, so neither side
// has to re-serialize JSON identically, and a reply can never be replayed as a request.
//
// Requests only name a monitor and an input id or a brightness/volume change. The receiving
// computer checks them against its own profiles, exactly as if its own user had asked, so a peer
// can never make it send bytes its profiles don't allow.

/// The shared pairing code, typed on both computers. 20 characters from a 31-letter alphabet
/// without look-alikes (no I, L, O, 0, 1), which is about 99 bits.
public enum PairingCode {
    public static let alphabet = Array("ABCDEFGHJKMNPQRSTUVWXYZ23456789")
    public static let length = 20
    public static let minimumLength = 16

    public static func generate() -> String {
        var generator = SystemRandomNumberGenerator()
        let chars = (0..<length).map { _ in alphabet[Int(generator.next(upperBound: UInt32(alphabet.count)))] }
        return format(String(chars))
    }

    /// Groups of four: "K7QF-2M9X-HC4T-8WPA-R3NB".
    public static func format(_ normalized: String) -> String {
        var groups: [String] = []
        var rest = Substring(normalized)
        while !rest.isEmpty {
            groups.append(String(rest.prefix(4)))
            rest = rest.dropFirst(4)
        }
        return groups.joined(separator: "-")
    }

    /// Upper case without spaces and dashes, or nil if it is too short or has other characters.
    public static func normalize(_ text: String) -> String? {
        let cleaned = text.uppercased().filter { !$0.isWhitespace && $0 != "-" }
        guard (minimumLength...64).contains(cleaned.count), cleaned.allSatisfy({ alphabet.contains($0) }) else { return nil }
        return cleaned
    }
}

/// The secret both computers derive from the pairing code.
public struct PeerKey: Equatable {
    public let bytes: [UInt8]

    public init?(pairingCode: String) {
        guard let normalized = PairingCode.normalize(pairingCode) else { return nil }
        bytes = Array(SHA256.hash(data: Data("MonitorSwitcher pairing v1\n\(normalized)".utf8)))
    }

    func mac(_ label: String, _ json: Data) -> [UInt8] {
        var message = Data("MSW1 \(label)\n".utf8)
        message.append(json)
        return Array(HMAC<SHA256>.authenticationCode(for: message, using: SymmetricKey(data: bytes)))
    }
}

public enum PeerRequestKind: String, Codable {
    case ping
    case switchInput = "switch"
    case control
}

/// Which monitor a request is about. The two computers may see different product codes (the LG
/// 32UN880 reports 0x774F over HDMI and 0x7750 over USB-C), so the serial number decides.
public struct PeerMonitorRef: Codable, Equatable {
    public var manufacturerId: String
    public var productCode: UInt16
    public var serialNumber: UInt32
    public var serialString: String?
    /// Profile id when a profile matched; used only when serials are missing.
    public var profileId: String?

    public init(manufacturerId: String, productCode: UInt16, serialNumber: UInt32, serialString: String? = nil, profileId: String? = nil) {
        self.manufacturerId = manufacturerId
        self.productCode = productCode
        self.serialNumber = serialNumber
        self.serialString = serialString
        self.profileId = profileId
    }

    public init(_ monitor: ResolvedMonitor) {
        self.init(manufacturerId: monitor.identity.manufacturerId, productCode: monitor.identity.productCode,
                  serialNumber: monitor.identity.serialNumber, serialString: monitor.identity.serialString,
                  profileId: monitor.needsSetup ? nil : monitor.profile.id)
    }

    /// Same maker and same serial number (or EDID serial text).
    public func sameSerial(_ identity: MonitorIdentity) -> Bool {
        guard manufacturerId.caseInsensitiveCompare(identity.manufacturerId) == .orderedSame else { return false }
        if serialNumber != 0 && serialNumber == identity.serialNumber { return true }
        let a = serialString?.trimmingCharacters(in: .whitespaces) ?? ""
        let b = identity.serialString?.trimmingCharacters(in: .whitespaces) ?? ""
        return !a.isEmpty && a.caseInsensitiveCompare(b) == .orderedSame
    }

    /// The local monitor this reference means, or nil when none or several could be meant.
    public func find(in monitors: [ResolvedMonitor]) -> ResolvedMonitor? {
        let bySerial = monitors.filter { sameSerial($0.identity) }
        if bySerial.count == 1 { return bySerial[0] }
        if bySerial.count > 1 {
            let exact = bySerial.filter { $0.identity.productCode == productCode }
            return exact.count == 1 ? exact[0] : nil
        }
        guard let profileId else { return nil }
        let byProfile = monitors.filter { !$0.needsSetup && $0.profile.id.caseInsensitiveCompare(profileId) == .orderedSame }
        return byProfile.count == 1 ? byProfile[0] : nil
    }
}

public struct PeerRequest: Codable, Equatable {
    public var v: Int = PeerWire.version
    /// Random per request; the receiver uses it to drop retries it has already handled.
    public var id: String
    /// Unix time in seconds; requests more than `PeerWire.maxClockSkew` away are refused.
    public var ts: Int64
    /// Sender's machine id, so a computer ignores its own broadcasts.
    public var machine: String
    /// Sender's computer name, for messages.
    public var from: String
    public var kind: PeerRequestKind
    public var monitor: PeerMonitorRef?
    public var inputId: String?
    public var control: String?
    public var setPercent: Int?
    public var adjustPercent: Int?

    public init(id: String = PeerWire.newId(), ts: Int64 = Int64(Date().timeIntervalSince1970), machine: String, from: String, kind: PeerRequestKind,
                monitor: PeerMonitorRef? = nil, inputId: String? = nil, control: String? = nil, setPercent: Int? = nil, adjustPercent: Int? = nil) {
        self.id = id
        self.ts = ts
        self.machine = machine
        self.from = from
        self.kind = kind
        self.monitor = monitor
        self.inputId = inputId
        self.control = control
        self.setPercent = setPercent
        self.adjustPercent = adjustPercent
    }

    public var controlChange: ControlChange? {
        if let setPercent { return .set(setPercent) }
        if let adjustPercent { return .adjust(adjustPercent) }
        return nil
    }
}

public struct PeerReply: Codable, Equatable {
    public var v: Int = PeerWire.version
    /// The request's id.
    public var id: String
    public var from: String
    public var ok: Bool
    public var message: String
    public var inputId: String?
    public var inputName: String?
    public var percent: Int?

    public init(id: String, from: String, ok: Bool, message: String, inputId: String? = nil, inputName: String? = nil, percent: Int? = nil) {
        self.id = id
        self.from = from
        self.ok = ok
        self.message = message
        self.inputId = inputId
        self.inputName = inputName
        self.percent = percent
    }
}

public enum PeerWire {
    public static let version = 1
    public static let maxClockSkew: Int64 = 120
    public static let maxDatagram = 8192
    private static let prefix = "MSW1 "

    public static func newId() -> String {
        var generator = SystemRandomNumberGenerator()
        return (0..<16).map { _ in String(format: "%02x", UInt8.random(in: 0...255, using: &generator)) }.joined()
    }

    public static func encode(_ request: PeerRequest, _ key: PeerKey) throws -> Data { try encode(request, "request", key) }
    public static func encode(_ reply: PeerReply, _ key: PeerKey) throws -> Data { try encode(reply, "reply", key) }
    public static func decodeRequest(_ data: Data, _ key: PeerKey) -> PeerRequest? { decode(data, "request", key) }
    public static func decodeReply(_ data: Data, _ key: PeerKey) -> PeerReply? { decode(data, "reply", key) }

    private static func encode<T: Encodable>(_ value: T, _ label: String, _ key: PeerKey) throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        let json = try encoder.encode(value)
        var data = Data((prefix + hex(key.mac(label, json)) + " ").utf8)
        data.append(json)
        return data
    }

    private static func decode<T: Decodable>(_ data: Data, _ label: String, _ key: PeerKey) -> T? {
        let bytes = [UInt8](data)
        let headerLength = prefix.utf8.count + 64 + 1
        guard bytes.count > headerLength, bytes.count <= maxDatagram,
              bytes.starts(with: Array(prefix.utf8)), bytes[headerLength - 1] == UInt8(ascii: " "),
              let mac = unhex(Array(bytes[prefix.utf8.count..<(headerLength - 1)])) else { return nil }
        let json = Data(bytes[headerLength...])
        guard constantTimeEqual(mac, key.mac(label, json)),
              let value = try? JSONDecoder().decode(T.self, from: json) else { return nil }
        return value
    }

    static func hex(_ bytes: [UInt8]) -> String { bytes.map { String(format: "%02x", $0) }.joined() }

    static func unhex(_ ascii: [UInt8]) -> [UInt8]? {
        guard ascii.count % 2 == 0 else { return nil }
        func nibble(_ c: UInt8) -> UInt8? {
            switch c {
            case UInt8(ascii: "0")...UInt8(ascii: "9"): return c - UInt8(ascii: "0")
            case UInt8(ascii: "a")...UInt8(ascii: "f"): return c - UInt8(ascii: "a") + 10
            case UInt8(ascii: "A")...UInt8(ascii: "F"): return c - UInt8(ascii: "A") + 10
            default: return nil
            }
        }
        var out: [UInt8] = []
        var i = 0
        while i < ascii.count {
            guard let hi = nibble(ascii[i]), let lo = nibble(ascii[i + 1]) else { return nil }
            out.append(hi << 4 | lo)
            i += 2
        }
        return out
    }

    static func constantTimeEqual(_ a: [UInt8], _ b: [UInt8]) -> Bool {
        guard a.count == b.count else { return false }
        var diff: UInt8 = 0
        for i in 0..<a.count { diff |= a[i] ^ b[i] }
        return diff == 0
    }
}

/// Receiver-side bookkeeping: refuses stale requests and answers retries from a cache instead of
/// acting twice. Not thread-safe; the listener uses it from one thread.
public final class PeerReplayGuard {
    private var seen: [String: (at: Date, reply: Data?)] = [:]
    private let keep: TimeInterval = 600

    public init() {}

    /// Seconds between the request's clock and ours when that exceeds `PeerWire.maxClockSkew`, else nil.
    public func clockSkew(_ request: PeerRequest, now: Date = Date()) -> Int64? {
        let skew = Int64(now.timeIntervalSince1970) - request.ts
        return abs(skew) > PeerWire.maxClockSkew ? skew : nil
    }

    /// True when this request id was handled before; `cached` is the reply sent then, if any.
    public func isDuplicate(_ request: PeerRequest, cached: inout Data?) -> Bool {
        guard let entry = seen[request.id] else { return false }
        cached = entry.reply
        return true
    }

    public func remember(_ request: PeerRequest, reply: Data?, now: Date = Date()) {
        seen = seen.filter { now.timeIntervalSince($0.value.at) < keep }
        seen[request.id] = (now, reply)
    }
}

/// Sends a request to the paired computer and waits for its reply. Implemented per platform (UDP).
public protocol PeerChannel: AnyObject {
    func send(_ request: PeerRequest) throws -> PeerReply
}

/// This computer, as named in requests.
public struct PeerMachine {
    public let id: String
    public let name: String

    public init(id: String, name: String) {
        self.id = id
        self.name = name
    }

    /// Reads this computer's machine id from state.json, creating it on first use.
    public static func load(_ state: JsonFileStore<AppState>, name: String) -> PeerMachine {
        if let id = state.load().machineId, !id.isEmpty {
            return PeerMachine(id: id, name: name)
        }
        let created = (try? state.update { s in
            if s.machineId?.isEmpty ?? true { s.machineId = PeerWire.newId() }
        })?.machineId
        return PeerMachine(id: created ?? PeerWire.newId(), name: name)
    }
}
