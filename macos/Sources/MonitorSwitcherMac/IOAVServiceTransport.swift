import Foundation
import IOKit
import MonitorSwitcherCore

#if arch(arm64)
// Private IOKit functions used by every Apple Silicon DDC tool (MonitorControl, m1ddc, BetterDisplay).
// Declared here because there is no public header.

@_silgen_name("IOAVServiceCreateWithService")
func IOAVServiceCreateWithService(_ allocator: CFAllocator?, _ service: io_service_t) -> Unmanaged<CFTypeRef>?

@_silgen_name("IOAVServiceReadI2C")
func IOAVServiceReadI2C(_ service: CFTypeRef, _ chipAddress: UInt32, _ offset: UInt32, _ outputBuffer: UnsafeMutableRawPointer, _ outputBufferSize: UInt32) -> IOReturn

@_silgen_name("IOAVServiceWriteI2C")
func IOAVServiceWriteI2C(_ service: CFTypeRef, _ chipAddress: UInt32, _ dataAddress: UInt32, _ inputBuffer: UnsafeMutableRawPointer, _ inputBufferSize: UInt32) -> IOReturn
#endif

/// DDC/CI on Apple Silicon through IOAVService. It takes the source byte as "data address" and can
/// send any of them, so it supports vendor protocols such as LG's. Each monitor is addressed by its
/// own AVService; nothing is ever broadcast.
public final class IOAVServiceTransport: DdcTransport {
    private let lock = NSLock()
    private let log: LogSink
    private static let ioSuccess: IOReturn = 0

    /// Pause between a Get-VCP request and reading the reply (DDC/CI asks for at least 40 ms).
    private static let replyDelayMicroseconds: UInt32 = 50_000

    public init(log: LogSink) {
        self.log = log
        super.init()
    }

    public override var id: String { "ioavservice" }
    public override var displayName: String { "Apple Silicon DDC/CI (IOAVService)" }
    public override var supportsRawI2C: Bool { true }
    public override var priority: Int { 10 }
    public override var supportsReads: Bool { true }

    public override func probe() -> TransportStatus {
        #if arch(arm64)
        return .available("Built into macOS on Apple Silicon.")
        #else
        return .unavailable("Intel Macs are not supported yet.")
        #endif
    }

    public override func enumerate() throws -> [DisplayEndpoint] {
        #if arch(arm64)
        return RegistryDisplays.external(log: log).map { display in
            let endpoint = DisplayEndpoint(transport: self, identity: display.identity, connectorId: display.connectorId, native: display.service)
            endpoint.osDisplayName = display.identity.modelName
            return endpoint
        }
        #else
        return []
        #endif
    }

    public override func deliverValidated(_ endpoint: DisplayEndpoint, _ frame: SetVcpFrame) throws {
        #if arch(arm64)
        guard let service = endpoint.native else {
            throw DdcError(.monitorNotFound, "\(endpoint.identity) has no AVService.")
        }
        var payload = frame.buildPayload()
        let length = UInt32(payload.count)
        let chipAddress = try Self.chipAddress()
        try DdcGuard.ensureMessage([DdcConstants.displayDeviceAddress, frame.sourceAddress] + payload)

        lock.lock()
        defer { lock.unlock() }
        // TODO: MonitorControl writes each command twice (10 ms apart) for flaky monitors. A single
        // write plus the profile's retries is enough for the LG 32UN880; revisit if reports come in.
        let result = IOAVServiceWriteI2C(service, chipAddress, UInt32(frame.sourceAddress), &payload, length)
        guard result == Self.ioSuccess else {
            throw DdcError(.monitorNotResponding,
                           "macOS could not deliver the command to \(endpoint.identity) (IOReturn 0x\(String(UInt32(bitPattern: result), radix: 16))).",
                           hint: DdcHints.ddcCiDisabled)
        }
        #else
        throw DdcError(.unsupportedByTransport, "Intel Macs are not supported yet.")
        #endif
    }

    /// Get VCP Feature. On the LG 32UN880 this goes silent while the monitor shows another input,
    /// which is how the app tells that the monitor isn't showing this Mac.
    public override func readVcp(_ endpoint: DisplayEndpoint, code: UInt8) -> VcpReading? {
        #if arch(arm64)
        guard let service = endpoint.native, let chipAddress = try? Self.chipAddress() else { return nil }
        var request = DdcRead.getVcpRequest(vcp: code)
        let requestLength = UInt32(request.count)
        guard (try? DdcGuard.ensureMessage([DdcConstants.displayDeviceAddress, DdcConstants.standardSourceAddress] + request)) != nil else { return nil }

        lock.lock()
        defer { lock.unlock() }
        guard IOAVServiceWriteI2C(service, chipAddress, UInt32(DdcConstants.standardSourceAddress), &request, requestLength) == Self.ioSuccess else {
            return nil
        }
        usleep(Self.replyDelayMicroseconds)
        // Reply: 0x6E 0x88 0x02 result vcp type max-hi max-lo cur-hi cur-lo checksum (+1 spare byte);
        // checksum = 0x50 ^ preceding bytes. Validated by DdcRead.parseVcpReply.
        var reply = [UInt8](repeating: 0, count: 12)
        let replyLength = UInt32(reply.count)
        guard IOAVServiceReadI2C(service, chipAddress, UInt32(DdcConstants.standardSourceAddress), &reply, replyLength) == Self.ioSuccess else {
            return nil
        }
        return DdcRead.parseVcpReply(reply, vcp: code)
        #else
        return nil
        #endif
    }

    public override func describe(_ endpoint: DisplayEndpoint) -> EndpointRecord? {
        EndpointRecord(transport: id, address: endpoint.connectorId)
    }

    /// IOAVService wants the 7-bit address. Derive it from the one constant the guard allows, so this
    /// transport can never address anything but the DDC/CI interface (never the 0x50/0xA0 EDID EEPROM).
    private static func chipAddress() throws -> UInt32 {
        try DdcGuard.ensureDeviceAddress(DdcConstants.displayDeviceAddress)
        let sevenBit = DdcConstants.displayDeviceAddress >> 1
        guard sevenBit == DdcConstants.displayDeviceAddress7Bit else {
            throw DdcError.safety("7-bit DDC/CI address mismatch.")
        }
        return UInt32(sevenBit)
    }
}

#if arch(arm64)
struct RegistryDisplay {
    let identity: MonitorIdentity
    let service: CFTypeRef
    let connectorId: String
}

/// Finds external displays the way MonitorControl's Arm64DDC does: walk the IORegistry service
/// plane, remember the identity of the last framebuffer seen ("AppleCLCD2" / "IOMobileFramebufferShim"),
/// and pair it with the next "DCPAVServiceProxy" whose Location is "External".
enum RegistryDisplays {
    static func external(log: LogSink) -> [RegistryDisplay] {
        var result: [RegistryDisplay] = []
        let root = IORegistryGetRootEntry(kIOMainPortDefault)
        defer { IOObjectRelease(root) }

        var iterator = io_iterator_t()
        guard IORegistryEntryCreateIterator(root, kIOServicePlane, IOOptionBits(kIORegistryIterateRecursively), &iterator) == KERN_SUCCESS else {
            log.warn("Could not walk the IORegistry.")
            return []
        }
        defer { IOObjectRelease(iterator) }

        var pending: MonitorIdentity?
        while case let entry = IOIteratorNext(iterator), entry != 0 {
            defer { IOObjectRelease(entry) }
            let name = registryName(entry)
            if name == "AppleCLCD2" || name == "IOMobileFramebufferShim" {
                pending = identity(of: entry)
            } else if name == "DCPAVServiceProxy" {
                guard stringProperty(entry, "Location") == "External", let identity = pending else { continue }
                pending = nil
                guard let service = IOAVServiceCreateWithService(kCFAllocatorDefault, entry)?.takeRetainedValue() else {
                    log.debug("No AVService for \(identity).")
                    continue
                }
                var entryId: UInt64 = 0
                IORegistryEntryGetRegistryEntryID(entry, &entryId)
                result.append(RegistryDisplay(identity: identity, service: service, connectorId: "AVService 0x\(String(entryId, radix: 16))"))
            }
        }
        return result
    }

    private static func registryName(_ entry: io_registry_entry_t) -> String {
        let buffer = UnsafeMutablePointer<CChar>.allocate(capacity: 128)
        defer { buffer.deallocate() }
        buffer.initialize(repeating: 0, count: 128)
        guard IORegistryEntryGetName(entry, buffer) == KERN_SUCCESS else { return "" }
        return String(cString: buffer)
    }

    private static func stringProperty(_ entry: io_registry_entry_t, _ key: String) -> String? {
        IORegistryEntryCreateCFProperty(entry, key as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue() as? String
    }

    /// "DisplayAttributes" → "ProductAttributes" holds the EDID identity. ProductID is the EDID
    /// product code and SerialNumber the numeric EDID serial, so stable ids match Windows.
    private static func identity(of entry: io_registry_entry_t) -> MonitorIdentity? {
        guard let attributes = IORegistryEntryCreateCFProperty(entry, "DisplayAttributes" as CFString, kCFAllocatorDefault, 0)?
                .takeRetainedValue() as? [String: Any],
              let product = attributes["ProductAttributes"] as? [String: Any],
              let maker = product["ManufacturerID"] as? String, maker.count == 3 else {
            return nil
        }
        let productId = (product["ProductID"] as? NSNumber)?.intValue ?? 0
        let serial = (product["SerialNumber"] as? NSNumber)?.int64Value ?? 0
        return MonitorIdentity(
            manufacturerId: maker.uppercased(),
            productCode: UInt16(truncatingIfNeeded: productId),
            serialNumber: UInt32(truncatingIfNeeded: serial),
            modelName: product["ProductName"] as? String,
            serialString: product["AlphanumericSerialNumber"] as? String)
    }
}
#endif
