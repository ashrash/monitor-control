import Foundation

/// Hex helpers for profile files, which store codes as strings like "0xD1". Mirrors `Hex.cs`.
public enum Hex {
    public static func parseUInt16(_ text: String?) -> UInt16? {
        guard var s = text?.trimmingCharacters(in: .whitespacesAndNewlines), !s.isEmpty else { return nil }
        if s.lowercased().hasPrefix("0x") {
            s = String(s.dropFirst(2))
        }
        guard (1...4).contains(s.count), s.allSatisfy({ $0.isHexDigit }) else { return nil }
        return UInt16(s, radix: 16)
    }

    /// Formats as 0xNN, or 0xNNNN when the value needs more than one byte.
    public static func format(_ value: UInt16) -> String {
        value <= 0xFF ? "0x" + pad(value, 2) : "0x" + pad(value, 4)
    }

    /// "0xNN" for a byte.
    public static func byte(_ value: UInt8) -> String { "0x" + pad(value, 2) }

    /// Upper-case hex with leading zeros.
    public static func pad<T: BinaryInteger>(_ value: T, _ digits: Int) -> String {
        let s = String(value, radix: 16, uppercase: true)
        return String(repeating: "0", count: max(0, digits - s.count)) + s
    }

    /// Parses "84 03 F4" style byte lists.
    public static func parseBytes(_ text: String) -> [UInt8]? {
        let tokens = text.split(whereSeparator: { $0 == " " || $0 == "," || $0 == "-" })
        var result: [UInt8] = []
        for token in tokens {
            var t = String(token)
            if t.lowercased().hasPrefix("0x") { t = String(t.dropFirst(2)) }
            guard let b = UInt8(t, radix: 16) else { return nil }
            result.append(b)
        }
        return result
    }

    public static func formatBytes(_ bytes: [UInt8]) -> String {
        bytes.map { pad($0, 2) }.joined(separator: " ")
    }
}
