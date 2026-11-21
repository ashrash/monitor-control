import Foundation

/// The identifying fields of an EDID base block. Mirrors `EdidInfo`.
public struct EdidInfo: Equatable {
    public let manufacturerId: String
    public let productCode: UInt16
    public let serialNumber: UInt32
    public let monitorName: String?
    public let serialString: String?
    public let manufactureWeek: Int
    public let manufactureYear: Int
    public let checksumValid: Bool
}

/// Parses the 128-byte EDID base block. Pure function: never touches hardware. Mirrors `EdidParser.cs`.
public enum EdidParser {
    private static let header: [UInt8] = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00]

    public static func parse(_ edid: [UInt8]) -> EdidInfo? {
        guard edid.count >= 128, Array(edid[0..<8]) == header else { return nil }
        guard let manufacturer = decodeManufacturerId(UInt16(edid[8]) << 8 | UInt16(edid[9])) else { return nil }

        let product = UInt16(edid[10]) | UInt16(edid[11]) << 8
        let serial = UInt32(edid[12]) | UInt32(edid[13]) << 8 | UInt32(edid[14]) << 16 | UInt32(edid[15]) << 24

        var name: String?
        var serialText: String?
        // Four 18-byte descriptors at 54, 72, 90, 108. Display descriptors start with 00 00 00 <tag>.
        for offset in stride(from: 54, through: 108, by: 18) {
            guard edid[offset] == 0, edid[offset + 1] == 0, edid[offset + 2] == 0 else { continue }
            let text = decodeDescriptorText(Array(edid[(offset + 5)..<(offset + 18)]))
            switch edid[offset + 3] {
            case 0xFC: if name == nil { name = text }
            case 0xFF: if serialText == nil { serialText = text }
            default: break
            }
        }

        var sum: UInt8 = 0
        for b in edid[0..<128] {
            sum = sum &+ b
        }

        return EdidInfo(
            manufacturerId: manufacturer,
            productCode: product,
            serialNumber: serial,
            monitorName: (name?.isEmpty ?? true) ? nil : name,
            serialString: (serialText?.isEmpty ?? true) ? nil : serialText,
            manufactureWeek: Int(edid[16]),
            manufactureYear: Int(edid[17]) + 1990,
            checksumValid: sum == 0)
    }

    /// Decodes the big-endian, 5-bits-per-letter PNP id ("GSM", "DEL", ...).
    public static func decodeManufacturerId(_ raw: UInt16) -> String? {
        var letters = ""
        for i in 0..<3 {
            let code = Int(raw >> UInt16(10 - 5 * i)) & 0x1F
            guard (1...26).contains(code) else { return nil }
            letters.append(Character(UnicodeScalar(UInt8(64 + code))))
        }
        return letters
    }

    private static func decodeDescriptorText(_ bytes: [UInt8]) -> String {
        let end = bytes.firstIndex(of: 0x0A) ?? bytes.count
        let text = String(decoding: bytes[0..<end], as: UTF8.self)
        var trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        while trimmed.hasSuffix("\0") { trimmed.removeLast() }
        return trimmed
    }
}

/// Who a monitor is, independent of which port or GPU it is plugged into. Mirrors `MonitorIdentity.cs`;
/// `stableId` has the same format on both platforms (e.g. "GSM-774F-0005DE50").
public struct MonitorIdentity: Codable, Hashable, CustomStringConvertible {
    public var manufacturerId: String
    public var productCode: UInt16
    public var serialNumber: UInt32
    public var modelName: String?
    public var serialString: String?

    public init(manufacturerId: String, productCode: UInt16, serialNumber: UInt32 = 0, modelName: String? = nil, serialString: String? = nil) {
        self.manufacturerId = manufacturerId
        self.productCode = productCode
        self.serialNumber = serialNumber
        self.modelName = modelName
        self.serialString = serialString
    }

    /// Falls back to the EDID serial string, then to maker+product.
    public var stableId: String {
        let basePart = "\(manufacturerId)-\(Hex.pad(productCode, 4))"
        if serialNumber != 0 {
            return "\(basePart)-\(Hex.pad(serialNumber, 8))"
        }
        if let serialString, !serialString.trimmingCharacters(in: .whitespaces).isEmpty {
            let clean = String(serialString.filter { $0.isLetter || $0.isNumber })
            if !clean.isEmpty {
                return "\(basePart)-\(clean)"
            }
        }
        return basePart
    }

    public static func fromEdid(_ edid: EdidInfo) -> MonitorIdentity {
        MonitorIdentity(manufacturerId: edid.manufacturerId, productCode: edid.productCode, serialNumber: edid.serialNumber,
                        modelName: edid.monitorName, serialString: edid.serialString)
    }

    public var description: String {
        "\(modelName ?? "Unknown monitor") (\(manufacturerId) 0x\(Hex.pad(productCode, 4)))"
    }
}

/// Parsed DDC/CI capabilities string, e.g. `(prot(monitor)type(lcd)model(U2720Q)vcp(10 12 60(0F 11 1B)))`.
public struct MonitorCapabilities {
    public let raw: String
    public let model: String?
    public let mccsVersion: String?
    public let vcp: [UInt8: [UInt16]]

    public func supportsVcp(_ code: UInt8) -> Bool { vcp[code] != nil }

    /// Values listed for a VCP code, or an empty list when none are listed.
    public func valuesFor(_ code: UInt8) -> [UInt16] { vcp[code] ?? [] }
}

/// Tolerant parser for capabilities strings. Mirrors `CapabilitiesParser.cs`. Real monitors get the
/// syntax wrong: the LG 32UN880 sends `type(lcd)UN880cmds(...)` with no `model(` keyword, so bare
/// text glued to a known keyword is treated as the model.
public enum CapabilitiesParser {
    private static let knownKeys = ["prot", "type", "model", "cmds", "vcp", "mccs_ver", "mswhql", "asset_eep", "mpu", "vcpname", "window"]

    public static func parse(_ raw: String) -> MonitorCapabilities {
        var text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        while text.hasSuffix("\0") { text.removeLast() }
        var chars = Array(text)
        if chars.first == "(" && findClosing(chars, 0) == chars.count - 1 {
            chars = Array(chars[1..<(chars.count - 1)])
        }

        var groups: [String: String] = [:] // lower-cased keys, first occurrence wins
        var bare = ""
        var i = 0
        while i < chars.count {
            if chars[i].isWhitespace || chars[i] == ")" {
                i += 1
                continue
            }

            let start = i
            while i < chars.count && chars[i] != "(" && chars[i] != ")" {
                i += 1
            }
            let token = String(chars[start..<i]).trimmingCharacters(in: .whitespacesAndNewlines)

            if i >= chars.count || chars[i] != "(" {
                bare += token
                continue
            }

            let close = findClosing(chars, i)
            let value = close < 0 ? String(chars[(i + 1)...]) : String(chars[(i + 1)..<close])
            i = close < 0 ? chars.count : close + 1

            let (prefix, key) = splitKnownKey(token)
            if !prefix.isEmpty {
                bare += prefix
            }
            let lowered = key.lowercased()
            if groups[lowered] == nil {
                groups[lowered] = value.trimmingCharacters(in: .whitespacesAndNewlines)
            }
        }

        var model = groups["model"]
        if (model?.trimmingCharacters(in: .whitespaces).isEmpty ?? true) && !bare.isEmpty {
            model = bare
        }
        let trimmedModel = model?.trimmingCharacters(in: .whitespacesAndNewlines)
        let vcp = groups["vcp"].map(parseVcp) ?? [:]
        return MonitorCapabilities(
            raw: raw,
            model: (trimmedModel?.isEmpty ?? true) ? nil : trimmedModel,
            mccsVersion: groups["mccs_ver"]?.trimmingCharacters(in: .whitespacesAndNewlines),
            vcp: vcp)
    }

    private static func splitKnownKey(_ token: String) -> (String, String) {
        let lowered = token.lowercased()
        for key in knownKeys.sorted(by: { $0.count > $1.count }) {
            if lowered == key {
                return ("", key)
            }
            if lowered.hasSuffix(key) {
                return (String(token.dropLast(key.count)), key)
            }
        }
        return ("", token)
    }

    private static func parseVcp(_ text: String) -> [UInt8: [UInt16]] {
        var result: [UInt8: [UInt16]] = [:]
        let chars = Array(text)
        var i = 0
        var last: UInt8?
        while i < chars.count {
            let c = chars[i]
            if c.isWhitespace {
                i += 1
                continue
            }

            if c == "(" {
                let close = findClosing(chars, i)
                let inner = close < 0 ? String(chars[(i + 1)...]) : String(chars[(i + 1)..<close])
                i = close < 0 ? chars.count : close + 1
                if let code = last {
                    result[code] = inner
                        .split(separator: " ")
                        .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
                        .filter { !$0.isEmpty }
                        .compactMap { UInt16($0, radix: 16) }
                }
                continue
            }

            let start = i
            while i < chars.count && !chars[i].isWhitespace && chars[i] != "(" {
                i += 1
            }
            if let code = UInt8(String(chars[start..<i]), radix: 16) {
                last = code
                if result[code] == nil {
                    result[code] = []
                }
            } else {
                last = nil
            }
        }
        return result
    }

    private static func findClosing(_ chars: [Character], _ openIndex: Int) -> Int {
        var depth = 0
        var i = openIndex
        while i < chars.count {
            if chars[i] == "(" {
                depth += 1
            } else if chars[i] == ")" {
                depth -= 1
                if depth == 0 { return i }
            }
            i += 1
        }
        return -1
    }
}
