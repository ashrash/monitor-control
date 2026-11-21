import Foundation

/// Fixed values from the VESA DDC/CI and MCCS standards. Mirrors `DdcConstants.cs`.
public enum DdcConstants {
    /// 8-bit I2C write address of a display's DDC/CI interface. The ONLY device address
    /// Monitor Switcher ever writes to. Transports never take a device address from callers.
    public static let displayDeviceAddress: UInt8 = 0x6E
    /// 7-bit form of `displayDeviceAddress` (what IOAVService expects as "chip address").
    public static let displayDeviceAddress7Bit: UInt8 = 0x37
    /// 8-bit address of the EDID EEPROM. Documented so the guard can name it; never written.
    public static let edidDeviceAddress: UInt8 = 0xA0
    /// Host source address used by every standard MCCS command.
    public static let standardSourceAddress: UInt8 = 0x51
    /// LG's alternative source address, used for its vendor input-select command.
    public static let lgAlternativeSourceAddress: UInt8 = 0x50
    public static let setVcpOpcode: UInt8 = 0x03
    public static let getVcpOpcode: UInt8 = 0x01
    public static let getVcpReplyOpcode: UInt8 = 0x02
    /// Length byte = 0x80 | number of message bytes that follow (excluding the checksum).
    public static let lengthFlag: UInt8 = 0x80
    /// Standard MCCS "Input Select" VCP code.
    public static let inputSelectVcp: UInt8 = 0x60
    /// Display-to-host replies are checksummed starting from 0x50 (the host's "virtual" address).
    public static let replyChecksumSeed: UInt8 = 0x50
}

/// A DDC/CI "Set VCP Feature" message. Mirrors `SetVcpFrame.cs`.
///
/// On the wire, after the I2C start condition:
/// `0x6E | source | 0x84 | 0x03 | vcp | value-hi | value-lo | checksum`
/// The checksum is the XOR of every preceding byte, including the 0x6E device address.
public struct SetVcpFrame: Hashable, CustomStringConvertible {
    public static let payloadLength = 6

    public let sourceAddress: UInt8
    public let vcpCode: UInt8
    public let value: UInt16

    public init(sourceAddress: UInt8, vcpCode: UInt8, value: UInt16) {
        self.sourceAddress = sourceAddress
        self.vcpCode = vcpCode
        self.value = value
    }

    /// Bytes that follow the source/register byte. IOAVService takes the source byte as its
    /// "data address" and this array as the buffer.
    public func buildPayload() -> [UInt8] {
        var payload = [UInt8](repeating: 0, count: SetVcpFrame.payloadLength)
        payload[0] = DdcConstants.lengthFlag | 4
        payload[1] = DdcConstants.setVcpOpcode
        payload[2] = vcpCode
        payload[3] = UInt8(value >> 8)
        payload[4] = UInt8(value & 0xFF)
        payload[5] = SetVcpFrame.computeChecksum(sourceAddress: sourceAddress, bytes: payload[0..<5])
        return payload
    }

    /// The full message including the 0x6E device address.
    public func buildMessage() -> [UInt8] {
        [DdcConstants.displayDeviceAddress, sourceAddress] + buildPayload()
    }

    /// XOR of the 0x6E device address, the source byte and `bytes`.
    public static func computeChecksum<S: Sequence>(sourceAddress: UInt8, bytes: S) -> UInt8 where S.Element == UInt8 {
        var checksum = DdcConstants.displayDeviceAddress ^ sourceAddress
        for b in bytes {
            checksum ^= b
        }
        return checksum
    }

    public var description: String {
        "SetVCP(source=\(Hex.byte(sourceAddress)), vcp=\(Hex.byte(vcpCode)), value=0x\(Hex.pad(value, 4)))"
    }
}

/// What went wrong, so UIs can choose an exit code, icon or wording. Mirrors `DdcErrorKind`.
public enum DdcErrorKind: String {
    case noSupportedTransport
    case driverMissing
    case monitorNotFound
    case ambiguousMonitor
    case monitorNotResponding
    /// The monitor is showing another input and ignores this computer until it comes back.
    case monitorOnOtherInput
    case inputNotInProfile
    case unsupportedByTransport
    /// A safety rule blocked the write. Always a bug or a bad profile, never a user mistake.
    case safetyViolation
    case transportFailure
    case configuration
    /// The paired computer did not answer, or refused the request.
    case peerUnavailable
}

public struct DdcError: Error, LocalizedError, CustomStringConvertible {
    public let kind: DdcErrorKind
    public let message: String
    /// What the user can do about it, in one sentence.
    public let hint: String?

    public init(_ kind: DdcErrorKind, _ message: String, hint: String? = nil) {
        self.kind = kind
        self.message = message
        self.hint = hint
    }

    public static func safety(_ message: String) -> DdcError {
        DdcError(.safetyViolation, message, hint: "This is a bug or a bad profile; nothing was sent.")
    }

    /// Message plus hint, ready to show to a person.
    public var userMessage: String { hint.map { "\(message) \($0)" } ?? message }
    public var errorDescription: String? { userMessage }
    public var description: String { message }
}

public enum DdcHints {
    public static let ddcCiDisabled =
        "Check that DDC/CI is turned on in the monitor's on-screen menu and that the cable carries DDC (most HDMI, DisplayPort and USB-C cables do; some adapters and docks do not)."
    public static let unsupportedPort =
        "On Apple Silicon, DDC/CI works over USB-C and DisplayPort; the built-in HDMI port of M1 and entry M2 Macs does not support it."
    public static let otherInput =
        "Pair this computer with the other one (Settings, Other computer) so it can switch back for you, or use the monitor's buttons."
}

/// The last line of defence before bytes reach a monitor. Mirrors `DdcGuard.cs`.
///
/// - The I2C device address must be 0x6E (DDC/CI). 0xA0 is the EDID EEPROM; writing to it can brick a monitor.
/// - The source/register byte must be 0x51 (standard) or 0x50 (LG alternative). 0x50 is only allowed in
///   this position, inside a DDC/CI message to 0x6E, never as a device address.
/// - Full messages must carry a correct checksum.
public enum DdcGuard {
    public static let allowedSourceAddresses: [UInt8] = [
        DdcConstants.standardSourceAddress,
        DdcConstants.lgAlternativeSourceAddress,
    ]

    public static func ensureDeviceAddress(_ deviceAddress: UInt8) throws {
        guard deviceAddress == DdcConstants.displayDeviceAddress else {
            throw DdcError.safety("Refused to write to I2C device address \(Hex.byte(deviceAddress)). Only the DDC/CI address 0x6E is ever written.")
        }
    }

    public static func ensureFrame(_ frame: SetVcpFrame) throws {
        guard allowedSourceAddresses.contains(frame.sourceAddress) else {
            throw DdcError.safety("Refused to send \(frame): source byte \(Hex.byte(frame.sourceAddress)) is not one of 0x51 or 0x50.")
        }
    }

    /// VCP codes the brightness/volume path may write. Input switching has its own path.
    public static let controlVcpCodes: [UInt8] = MonitorControl.allCases.map(\.vcpCode)

    /// Brightness and volume writes: standard source byte and one of `controlVcpCodes` only.
    public static func ensureControlFrame(_ frame: SetVcpFrame) throws {
        try ensureFrame(frame)
        guard frame.sourceAddress == DdcConstants.standardSourceAddress, controlVcpCodes.contains(frame.vcpCode) else {
            throw DdcError.safety("Refused to send \(frame): only brightness (0x10) and volume (0x62) may be set this way.")
        }
    }

    /// Checks a complete message (device address first, checksum last).
    public static func ensureMessage(_ message: [UInt8]) throws {
        guard message.count >= 3 else {
            throw DdcError.safety("Refused to send a DDC/CI message shorter than 3 bytes.")
        }
        try ensureDeviceAddress(message[0])
        guard allowedSourceAddresses.contains(message[1]) else {
            throw DdcError.safety("Refused to send a DDC/CI message with source byte \(Hex.byte(message[1])).")
        }
        var xor: UInt8 = 0
        for b in message.dropLast() {
            xor ^= b
        }
        guard xor == message[message.count - 1] else {
            throw DdcError.safety("Refused to send a DDC/CI message with checksum \(Hex.byte(message[message.count - 1])); expected \(Hex.byte(xor)).")
        }
    }
}

/// Get VCP Feature: request builder and reply parser. Platform-neutral so it can be unit tested;
/// the macOS transport uses it for reads (Windows uses dxva2, which does this internally).
public enum DdcRead {
    /// Bytes after the 0x51 source byte: `0x82 0x01 vcp checksum`.
    public static func getVcpRequest(vcp: UInt8) -> [UInt8] {
        var payload: [UInt8] = [DdcConstants.lengthFlag | 2, DdcConstants.getVcpOpcode, vcp, 0]
        payload[3] = SetVcpFrame.computeChecksum(sourceAddress: DdcConstants.standardSourceAddress, bytes: payload[0..<3])
        return payload
    }

    /// Parses a "VCP Feature Reply":
    /// `0x6E 0x88 0x02 result vcp type max-hi max-lo cur-hi cur-lo checksum`
    /// where checksum = 0x50 ^ (XOR of all preceding bytes). Some I2C stacks drop the leading
    /// 0x6E source byte; that form is accepted too. Returns nil for anything malformed, for an
    /// "unsupported VCP code" result, or for a reply about a different code.
    public static func parseVcpReply(_ raw: [UInt8], vcp: UInt8) -> VcpReading? {
        var bytes = raw
        if bytes.first == (DdcConstants.lengthFlag | 8) {
            bytes.insert(DdcConstants.displayDeviceAddress, at: 0)
        }
        guard bytes.count >= 11,
              bytes[0] == DdcConstants.displayDeviceAddress,
              bytes[1] == (DdcConstants.lengthFlag | 8),
              bytes[2] == DdcConstants.getVcpReplyOpcode,
              bytes[3] == 0x00,
              bytes[4] == vcp else {
            return nil
        }
        var xor = DdcConstants.replyChecksumSeed
        for i in 0..<10 {
            xor ^= bytes[i]
        }
        guard xor == bytes[10] else { return nil }
        let maximum = UInt16(bytes[6]) << 8 | UInt16(bytes[7])
        let current = UInt16(bytes[8]) << 8 | UInt16(bytes[9])
        return VcpReading(current: current, maximum: maximum)
    }
}
