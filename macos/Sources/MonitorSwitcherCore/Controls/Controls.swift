import Foundation

/// Picture and sound settings Monitor Switcher can change besides the input. Mirrors `MonitorControl.cs`.
///
/// These are the only VCP codes ever written outside input switching; `DdcGuard.ensureControlFrame`
/// enforces that, so a bug elsewhere cannot turn this path into a general "write any VCP code" tool.
public enum MonitorControl: String, CaseIterable, Codable {
    case brightness = "Brightness"
    case volume = "Volume"

    /// MCCS VCP code: 0x10 Luminance, 0x62 Audio speaker volume.
    public var vcpCode: UInt8 {
        switch self {
        case .brightness: return 0x10
        case .volume: return 0x62
        }
    }

    public var displayName: String { rawValue }

    /// Case-insensitive lookup by name ("brightness", "Volume").
    public static func named(_ name: String) -> MonitorControl? {
        allCases.first { $0.rawValue.caseInsensitiveCompare(name) == .orderedSame }
    }
}

/// A requested change: an absolute percentage, or a step up or down from the current value.
public enum ControlChange: Equatable, CustomStringConvertible {
    case set(Int)
    case adjust(Int)

    /// Default step for "brighter", "louder" and so on.
    public static let step = 10

    /// "70", "70%", "+10", "-10".
    public static func parse(_ text: String) -> ControlChange? {
        var t = text.trimmingCharacters(in: .whitespaces)
        if t.hasSuffix("%") { t.removeLast() }
        guard !t.isEmpty else { return nil }
        if t.hasPrefix("+") || t.hasPrefix("-") {
            guard let delta = Int(t), abs(delta) <= 100 else { return nil }
            return .adjust(delta)
        }
        guard let value = Int(t), (0...100).contains(value) else { return nil }
        return .set(value)
    }

    /// The percentage after applying this change to `current` (0-100).
    public func apply(to current: Int?) -> Int? {
        switch self {
        case .set(let value): return ControlMath.clamp(value)
        case .adjust(let delta): return current.map { ControlMath.clamp($0 + delta) }
        }
    }

    public var description: String {
        switch self {
        case .set(let value): return "\(value)%"
        case .adjust(let delta): return delta >= 0 ? "+\(delta)%" : "\(delta)%"
        }
    }
}

/// Converts between percentages and the monitor's own range. Monitors report their maximum
/// (usually 100); a maximum of 0 is treated as 100.
public enum ControlMath {
    public static func clamp(_ percent: Int) -> Int { min(100, max(0, percent)) }

    public static func percent(_ reading: VcpReading) -> Int {
        let maximum = effectiveMaximum(reading.maximum)
        return clamp(Int((Double(min(reading.current, maximum)) * 100 / Double(maximum)).rounded()))
    }

    public static func value(percent: Int, maximum: UInt16) -> UInt16 {
        let maximum = effectiveMaximum(maximum)
        return UInt16((Double(clamp(percent)) * Double(maximum) / 100).rounded())
    }

    public static func effectiveMaximum(_ maximum: UInt16) -> UInt16 { maximum == 0 ? 100 : maximum }
}

/// What a brightness or volume change did.
public struct ControlOutcome {
    public let monitor: ResolvedMonitor
    public let control: MonitorControl
    public let percent: Int
    public let endpoint: DisplayEndpoint
}
