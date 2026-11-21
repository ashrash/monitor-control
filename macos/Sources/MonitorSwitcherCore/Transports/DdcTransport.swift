import Foundation

public struct TransportStatus {
    public let isAvailable: Bool
    public let summary: String
    public let hint: String?

    public static func available(_ summary: String) -> TransportStatus {
        TransportStatus(isAvailable: true, summary: summary, hint: nil)
    }

    public static func unavailable(_ summary: String, hint: String? = nil) -> TransportStatus {
        TransportStatus(isAvailable: false, summary: summary, hint: hint)
    }
}

public struct VcpReading: Equatable {
    public let current: UInt16
    public let maximum: UInt16

    public init(current: UInt16, maximum: UInt16) {
        self.current = current
        self.maximum = maximum
    }
}

/// A monitor as seen through one transport. Mirrors `DisplayEndpoint.cs`.
public final class DisplayEndpoint: CustomStringConvertible {
    public let transport: DdcTransport
    public let identity: MonitorIdentity
    /// Transport-specific location, e.g. "AVService 0x10000047a".
    public let connectorId: String
    /// OS display name when known (on macOS: the product name).
    public var osDisplayName: String?
    /// True when the OS no longer lists this monitor and it was rebuilt from the last known address.
    public let isRestoredFromCache: Bool
    /// Transport-private handle (an IOAVService on macOS).
    public let native: AnyObject?

    public init(transport: DdcTransport, identity: MonitorIdentity, connectorId: String, native: AnyObject?, isRestoredFromCache: Bool = false) {
        self.transport = transport
        self.identity = identity
        self.connectorId = connectorId
        self.native = native
        self.isRestoredFromCache = isRestoredFromCache
    }

    public var description: String { "\(identity) via \(transport.id) \(connectorId)" }
}

/// Layer 1: moves DDC/CI bytes to a monitor. Knows nothing about which bytes switch inputs.
/// Mirrors `DdcTransport.cs`.
///
/// `send` is final: it runs the safety guard and then calls `deliverValidated`, so no transport can
/// skip the checks. Implementations must also call `DdcGuard.ensureDeviceAddress` right before their
/// native write, and must never accept a device address from outside.
open class DdcTransport {
    public init() {}

    /// Short id, e.g. "ioavservice".
    open var id: String { fatalError("Subclasses must override id") }
    open var displayName: String { fatalError("Subclasses must override displayName") }
    /// True if the transport can write arbitrary DDC/CI frames (any source byte).
    open var supportsRawI2C: Bool { fatalError("Subclasses must override supportsRawI2C") }
    /// Lower is preferred when several transports reach the same monitor.
    open var priority: Int { 100 }
    /// True if `readVcp` is implemented, so nil means "no answer".
    open var supportsReads: Bool { false }

    /// Is the interface present and usable? Must not throw.
    open func probe() -> TransportStatus { fatalError("Subclasses must override probe()") }

    /// Monitors this transport can reach right now.
    open func enumerate() throws -> [DisplayEndpoint] { fatalError("Subclasses must override enumerate()") }

    public final func canSend(_ inputProtocol: any InputProtocol) -> Bool {
        supportsRawI2C || !inputProtocol.requiresRawI2C
    }

    public final func canSend(_ frame: SetVcpFrame) -> Bool {
        supportsRawI2C || frame.sourceAddress == DdcConstants.standardSourceAddress
    }

    /// The only public write path.
    public final func send(_ endpoint: DisplayEndpoint, _ frame: SetVcpFrame) throws {
        guard endpoint.transport === self else {
            throw DdcError(.configuration, "The endpoint belongs to a different transport.")
        }
        try DdcGuard.ensureFrame(frame)
        guard canSend(frame) else {
            throw DdcError(.unsupportedByTransport, "\(displayName) can only send standard MCCS commands, not \(frame).")
        }
        try deliverValidated(endpoint, frame)
    }

    /// Delivers an already validated frame. Called only by `send`; never call it directly.
    open func deliverValidated(_ endpoint: DisplayEndpoint, _ frame: SetVcpFrame) throws {
        fatalError("Subclasses must override deliverValidated")
    }

    /// The DDC/CI capabilities string, or nil if unsupported or unreadable.
    open func readCapabilities(_ endpoint: DisplayEndpoint) -> String? { nil }

    /// A VCP value, or nil if unsupported or unreadable.
    open func readVcp(_ endpoint: DisplayEndpoint, code: UInt8) -> VcpReading? { nil }

    /// A record that lets `restore` find the endpoint again later.
    open func describe(_ endpoint: DisplayEndpoint) -> EndpointRecord? { nil }

    /// Rebuilds an endpoint the OS no longer lists.
    open func restore(_ record: EndpointRecord, identity: MonitorIdentity) -> DisplayEndpoint? { nil }
}
