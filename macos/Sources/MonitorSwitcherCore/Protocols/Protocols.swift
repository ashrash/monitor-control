import Foundation

/// A code worth trying for a port during the setup wizard.
public struct InputCandidate: Hashable {
    /// Canonical port id: hdmi1, hdmi2, dp1, dp2, usbc, ...
    public let inputId: String
    public let name: String
    public let code: UInt16

    public init(_ inputId: String, _ name: String, _ code: UInt16) {
        self.inputId = inputId
        self.name = name
        self.code = code
    }
}

/// One frame to send, then an optional pause.
public struct DdcStep {
    public let frame: SetVcpFrame
    public let delayAfterMs: Int

    public init(_ frame: SetVcpFrame, delayAfterMs: Int = 0) {
        self.frame = frame
        self.delayAfterMs = delayAfterMs
    }
}

/// Layer 2: decides WHAT bytes switch a monitor's input. Knows nothing about GPUs or drivers.
/// Mirrors `InputProtocol.cs`. To add a vendor, adopt `SetVcpProtocol` (if the vendor only changes
/// the source byte or VCP code) or this protocol, then register it in `ProtocolRegistry`.
public protocol InputProtocol: AnyObject {
    /// Stable id used in profile files, e.g. "lg-alternative".
    var id: String { get }
    var displayName: String { get }
    /// True when frames need a raw I2C transport. (On macOS the only transport, IOAVService, is raw.)
    var requiresRawI2C: Bool { get }
    /// Every code this protocol may send during the setup wizard, per port.
    var candidates: [InputCandidate] { get }
    func buildSwitch(code: UInt16) -> [DdcStep]
    /// How promising this protocol is for a monitor without a profile (0 = don't try by default).
    func rank(for monitor: MonitorIdentity, capabilities: MonitorCapabilities?) -> Int
    /// Candidates narrowed (or extended) using what the monitor reports about itself.
    func candidates(for capabilities: MonitorCapabilities?) -> [InputCandidate]
}

/// Single "Set VCP Feature" frame protocols. Most vendor variants only differ in source byte and VCP code.
public protocol SetVcpProtocol: InputProtocol {
    var sourceAddress: UInt8 { get }
    var vcpCode: UInt8 { get }
}

public extension SetVcpProtocol {
    var requiresRawI2C: Bool { sourceAddress != DdcConstants.standardSourceAddress }

    func buildSwitch(code: UInt16) -> [DdcStep] {
        [DdcStep(SetVcpFrame(sourceAddress: sourceAddress, vcpCode: vcpCode, value: code))]
    }
}

/// VESA MCCS "Input Select" (VCP 0x60, source 0x51).
public final class StandardMccsProtocol: SetVcpProtocol {
    public static let protocolId = "mccs-standard"

    private static let knownInputs: [InputCandidate] = [
        InputCandidate("hdmi1", "HDMI 1", 0x11),
        InputCandidate("hdmi2", "HDMI 2", 0x12),
        InputCandidate("dp1", "DisplayPort 1", 0x0F),
        InputCandidate("dp2", "DisplayPort 2", 0x10),
        InputCandidate("usbc", "USB-C", 0x1B), // common vendor extension (Dell and others)
        InputCandidate("usbc", "USB-C", 0x10), // some monitors expose USB-C as DisplayPort 2
        InputCandidate("dvi1", "DVI 1", 0x03),
        InputCandidate("dvi2", "DVI 2", 0x04),
        InputCandidate("vga1", "VGA", 0x01),
    ]

    public init() {}

    public var id: String { Self.protocolId }
    public var displayName: String { "Standard MCCS (VCP 0x60)" }
    public var sourceAddress: UInt8 { DdcConstants.standardSourceAddress }
    public var vcpCode: UInt8 { DdcConstants.inputSelectVcp }
    public var candidates: [InputCandidate] { Self.knownInputs }

    public func rank(for monitor: MonitorIdentity, capabilities: MonitorCapabilities?) -> Int {
        capabilities == nil || capabilities!.supportsVcp(DdcConstants.inputSelectVcp) ? 10 : 1
    }

    /// If the monitor lists its 0x60 values, only those are tried; unknown listed values are added
    /// as generic "Input 0xNN" candidates so the wizard can still offer them.
    public func candidates(for capabilities: MonitorCapabilities?) -> [InputCandidate] {
        guard let listedValues = capabilities?.valuesFor(DdcConstants.inputSelectVcp).filter({ $0 != 0 }), !listedValues.isEmpty else {
            return Self.knownInputs
        }
        let listed = Set(listedValues)
        var result = Self.knownInputs.filter { listed.contains($0.code) }
        for code in listed.filter({ code in !Self.knownInputs.contains { $0.code == code } }).sorted() {
            result.append(InputCandidate("input-\(Hex.pad(code, 2).lowercased())", "Input \(Hex.format(code))", code))
        }
        return result
    }
}

/// LG's vendor input select: VCP 0xF4 sent with source byte 0x50 instead of 0x51. Needed on many LG
/// monitors (e.g. 32UN880) where the standard command blanks the screen or can't reach USB-C.
public final class LgAlternativeProtocol: SetVcpProtocol {
    public static let protocolId = "lg-alternative"
    public static let lgVcpCode: UInt8 = 0xF4

    private static let knownInputs: [InputCandidate] = [
        InputCandidate("hdmi1", "HDMI 1", 0x90),
        InputCandidate("hdmi2", "HDMI 2", 0x91),
        InputCandidate("hdmi3", "HDMI 3", 0x92),
        InputCandidate("dp1", "DisplayPort 1", 0xD0),
        InputCandidate("dp2", "DisplayPort 2", 0xD1),
        InputCandidate("usbc", "USB-C", 0xD1),
        InputCandidate("usbc", "USB-C", 0xD2),
        InputCandidate("usbc", "USB-C", 0xD3),
    ]

    public init() {}

    public var id: String { Self.protocolId }
    public var displayName: String { "LG alternative (VCP 0xF4, source 0x50)" }
    public var sourceAddress: UInt8 { DdcConstants.lgAlternativeSourceAddress }
    public var vcpCode: UInt8 { Self.lgVcpCode }
    public var candidates: [InputCandidate] { Self.knownInputs }

    public func rank(for monitor: MonitorIdentity, capabilities: MonitorCapabilities?) -> Int {
        guard monitor.manufacturerId == "GSM" else { return 0 }
        return capabilities?.supportsVcp(Self.lgVcpCode) == true ? 70 : 50
    }

    public func candidates(for capabilities: MonitorCapabilities?) -> [InputCandidate] { Self.knownInputs }
}

/// All known protocols. Add new vendor protocols to `all`.
public enum ProtocolRegistry {
    public static let standard: any InputProtocol = StandardMccsProtocol()
    public static let all: [any InputProtocol] = [standard, LgAlternativeProtocol()]

    public static func find(_ id: String?) -> (any InputProtocol)? {
        guard let id else { return nil }
        return all.first { $0.id.caseInsensitiveCompare(id) == .orderedSame }
    }

    public static func get(_ id: String) throws -> any InputProtocol {
        guard let found = find(id) else {
            throw DdcError(.configuration, "Unknown protocol '\(id)'. Known protocols: \(all.map { $0.id }.joined(separator: ", ")).")
        }
        return found
    }

    /// Protocols worth trying on an unknown monitor, most promising first (stable for equal ranks).
    public static func ranked(for monitor: MonitorIdentity, capabilities: MonitorCapabilities?) -> [any InputProtocol] {
        all.enumerated()
            .map { (index: $0.offset, item: $0.element, rank: $0.element.rank(for: monitor, capabilities: capabilities)) }
            .filter { $0.rank > 0 }
            .sorted { $0.rank != $1.rank ? $0.rank > $1.rank : $0.index < $1.index }
            .map { $0.item }
    }
}
