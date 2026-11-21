import Darwin
import Foundation
import MonitorSwitcherCore

/// UDP transport for requests to the paired computer. Mirrors `UdpPeerChannel.cs`.
///
/// A request goes to the address the peer last answered from and is also broadcast on every local
/// IPv4 network, so it still arrives after the other computer gets a new address. It is resent
/// until a reply arrives or the timeout passes; the receiver answers retries from a cache.
public final class UdpPeerChannel: PeerChannel {
    private let key: PeerKey
    private let port: UInt16
    private let state: JsonFileStore<AppState>
    private let log: LogSink
    private let timeout: TimeInterval
    private let resendInterval: TimeInterval = 0.6

    public init(key: PeerKey, port: UInt16, state: JsonFileStore<AppState>, log: LogSink, timeout: TimeInterval = 4) {
        self.key = key
        self.port = port
        self.state = state
        self.log = log
        self.timeout = timeout
    }

    public func send(_ request: PeerRequest) throws -> PeerReply {
        let datagram = try PeerWire.encode(request, key)
        let fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard fd >= 0 else { throw unavailable("Could not open a network socket (\(String(cString: strerror(errno)))).") }
        defer { close(fd) }
        var on: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_BROADCAST, &on, socklen_t(MemoryLayout<Int32>.size))

        var targets: [String] = []
        if let last = state.load().peer?.lastAddress { targets.append(last) }
        for address in Network.broadcastAddresses() + ["255.255.255.255"] where !targets.contains(address) {
            targets.append(address)
        }
        log.debug("Sending \(request.kind.rawValue) to the paired computer via \(targets.joined(separator: ", ")):\(port).")

        let deadline = Date().addingTimeInterval(timeout)
        var lastError: String?
        while Date() < deadline {
            for target in targets {
                if let error = Network.sendTo(fd, datagram, host: target, port: port) { lastError = error }
            }
            let waitUntil = min(deadline, Date().addingTimeInterval(resendInterval))
            while let remaining = Optional(waitUntil.timeIntervalSinceNow), remaining > 0 {
                guard let (data, sender) = Network.receive(fd, timeout: remaining) else { break }
                guard let reply = PeerWire.decodeReply(data, key), reply.id == request.id else { continue }
                remember(sender, reply.from)
                return reply
            }
        }
        if let lastError { log.debug("Last send error: \(lastError)") }
        throw unavailable("The other computer did not answer.")
    }

    private func remember(_ address: String, _ name: String) {
        _ = try? state.update { s in
            let peer = s.peer ?? PeerState()
            peer.lastAddress = address
            peer.lastName = name
            peer.lastSeenAt = Date()
            s.peer = peer
        }
    }

    private func unavailable(_ message: String) -> DdcError {
        DdcError(.peerUnavailable, message,
                 hint: "Check that Monitor Switcher is running there with the same pairing code, that both computers are on the same network, and that its firewall allows Monitor Switcher (UDP port \(port)).")
    }
}

/// Answers requests from the paired computer. Runs one background thread; each request is handled
/// to completion before the next is read, so retries of the same request are answered from cache.
public final class UdpPeerListener {
    public typealias Handler = (PeerRequest) -> PeerReply

    private let port: UInt16
    private let machine: PeerMachine
    private let keyProvider: () -> PeerKey?
    private let handler: Handler
    private let log: LogSink
    private let replay = PeerReplayGuard()
    private var fd: Int32 = -1
    private var thread: Thread?
    private let stopLock = NSLock()
    private var stopped = false

    public init(port: UInt16, machine: PeerMachine, log: LogSink, keyProvider: @escaping () -> PeerKey?, handler: @escaping Handler) {
        self.port = port
        self.machine = machine
        self.log = log
        self.keyProvider = keyProvider
        self.handler = handler
    }

    public func start() throws {
        let fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard fd >= 0 else { throw DdcError(.peerUnavailable, "Could not open a network socket.") }
        var on: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &on, socklen_t(MemoryLayout<Int32>.size))
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = port.bigEndian
        address.sin_addr = in_addr(s_addr: INADDR_ANY)
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        guard bound == 0 else {
            let reason = String(cString: strerror(errno))
            close(fd)
            throw DdcError(.peerUnavailable, "Could not listen on UDP port \(port): \(reason).",
                           hint: "Another program may be using it; set a different \"port\" under \"peer\" in settings.json on both computers.")
        }
        self.fd = fd
        let thread = Thread { [weak self] in self?.run() }
        thread.name = "MonitorSwitcher peer listener"
        self.thread = thread
        thread.start()
        log.info("Listening for the paired computer on UDP port \(port).")
    }

    public func stop() {
        stopLock.lock()
        stopped = true
        stopLock.unlock()
    }

    private var isStopped: Bool {
        stopLock.lock()
        defer { stopLock.unlock() }
        return stopped
    }

    private func run() {
        defer { close(fd) }
        while !isStopped {
            guard let (data, sender, senderPort) = Network.receiveWithPort(fd, timeout: 1) else { continue }
            guard let key = keyProvider(), let request = PeerWire.decodeRequest(data, key) else { continue }
            if request.machine == machine.id { continue }

            var cached: Data?
            if replay.isDuplicate(request, cached: &cached) {
                if let cached { _ = Network.sendTo(fd, cached, host: sender, port: senderPort) }
                continue
            }

            let reply: PeerReply
            if let skew = replay.clockSkew(request) {
                reply = PeerReply(id: request.id, from: machine.name, ok: false,
                                  message: "The clocks of the two computers differ by \(abs(skew)) seconds. Turn on automatic date and time on both.")
                log.warn("Refused a request from \(request.from) at \(sender): clocks differ by \(skew) s.")
            } else {
                log.info("Request from \(request.from) at \(sender): \(request.kind.rawValue)\(request.inputId.map { " \($0)" } ?? "")\(request.control.map { " \($0)" } ?? "").")
                reply = handler(request)
            }
            let encoded = try? PeerWire.encode(reply, key)
            replay.remember(request, reply: encoded)
            if let encoded { _ = Network.sendTo(fd, encoded, host: sender, port: senderPort) }
        }
    }
}

/// Small BSD socket helpers for IPv4 UDP.
enum Network {
    /// Directed broadcast address of every IPv4 interface that is up and not loopback.
    static func broadcastAddresses() -> [String] {
        var result: [String] = []
        var list: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&list) == 0, let first = list else { return result }
        defer { freeifaddrs(list) }
        var cursor: UnsafeMutablePointer<ifaddrs>? = first
        while let entry = cursor {
            let flags = Int32(entry.pointee.ifa_flags)
            if let addr = entry.pointee.ifa_addr, addr.pointee.sa_family == sa_family_t(AF_INET),
               flags & IFF_UP != 0, flags & IFF_LOOPBACK == 0, flags & IFF_BROADCAST != 0,
               let broadcast = entry.pointee.ifa_dstaddr, broadcast.pointee.sa_family == sa_family_t(AF_INET) {
                let text = broadcast.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { ipString($0.pointee.sin_addr) }
                if !result.contains(text) { result.append(text) }
            }
            cursor = entry.pointee.ifa_next
        }
        return result
    }

    /// Returns an error description, or nil on success.
    static func sendTo(_ fd: Int32, _ data: Data, host: String, port: UInt16) -> String? {
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = port.bigEndian
        guard inet_pton(AF_INET, host, &address.sin_addr) == 1 else { return "bad address \(host)" }
        let sent = data.withUnsafeBytes { buffer in
            withUnsafePointer(to: &address) {
                $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    sendto(fd, buffer.baseAddress, buffer.count, 0, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
        }
        return sent < 0 ? "\(host): \(String(cString: strerror(errno)))" : nil
    }

    static func receive(_ fd: Int32, timeout: TimeInterval) -> (Data, String)? {
        receiveWithPort(fd, timeout: timeout).map { ($0.0, $0.1) }
    }

    static func receiveWithPort(_ fd: Int32, timeout: TimeInterval) -> (Data, String, UInt16)? {
        var poller = pollfd(fd: fd, events: Int16(POLLIN), revents: 0)
        guard poll(&poller, 1, Int32(max(1, timeout * 1000))) > 0 else { return nil }
        var buffer = [UInt8](repeating: 0, count: PeerWire.maxDatagram + 1)
        var sender = sockaddr_in()
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        let count = withUnsafeMutablePointer(to: &sender) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { recvfrom(fd, &buffer, buffer.count, 0, $0, &length) }
        }
        guard count > 0, count <= PeerWire.maxDatagram else { return nil }
        return (Data(buffer[0..<count]), ipString(sender.sin_addr), UInt16(bigEndian: sender.sin_port))
    }

    static func ipString(_ address: in_addr) -> String {
        var address = address
        var text = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
        inet_ntop(AF_INET, &address, &text, socklen_t(INET_ADDRSTRLEN))
        return String(cString: text)
    }
}

extension MacPlatform {
    /// "Ashwin's MacBook Pro" style name shown on the other computer.
    public static func computerName() -> String {
        Host.current().localizedName ?? ProcessInfo.processInfo.hostName
    }

    /// Actions with relaying to the paired computer when settings contain a valid pairing code.
    public static func actions(_ services: AppServices, settings: AppSettings) -> MonitorActions {
        let machine = PeerMachine.load(services.state, name: computerName())
        let channel = settings.peer?.key.map {
            UdpPeerChannel(key: $0, port: settings.peer?.effectivePort ?? UInt16(PeerSettings.defaultPort), state: services.state, log: services.log)
        }
        return MonitorActions(services: services, machine: machine, peer: channel)
    }
}
