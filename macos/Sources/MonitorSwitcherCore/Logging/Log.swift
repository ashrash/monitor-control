import Foundation

public enum LogLevel: Int, Comparable {
    case debug, info, warn, error

    public static func < (lhs: LogLevel, rhs: LogLevel) -> Bool { lhs.rawValue < rhs.rawValue }

    public var label: String {
        switch self {
        case .debug: return "DEBUG"
        case .info: return "INFO"
        case .warn: return "WARN"
        case .error: return "ERROR"
        }
    }
}

/// Minimal logging abstraction (`ILog` on Windows). The macOS implementation writes a rotating file.
public protocol LogSink: AnyObject {
    func write(_ level: LogLevel, _ message: String, _ error: Error?)
}

public extension LogSink {
    func debug(_ message: String) { write(.debug, message, nil) }
    func info(_ message: String) { write(.info, message, nil) }
    func warn(_ message: String, _ error: Error? = nil) { write(.warn, message, error) }
    func error(_ message: String, _ error: Error? = nil) { write(.error, message, error) }
}

public final class NullLog: LogSink {
    public static let instance = NullLog()
    public init() {}
    public func write(_ level: LogLevel, _ message: String, _ error: Error?) {}
}

/// Writes to several logs.
public final class TeeLog: LogSink {
    private let logs: [LogSink]
    public init(_ logs: [LogSink]) { self.logs = logs }
    public func write(_ level: LogLevel, _ message: String, _ error: Error?) {
        for log in logs { log.write(level, message, error) }
    }
}

/// Collects log lines in memory (tests).
public final class MemoryLog: LogSink {
    private let lock = NSLock()
    private var storage: [String] = []
    public init() {}

    public var lines: [String] {
        lock.lock()
        defer { lock.unlock() }
        return storage
    }

    public func write(_ level: LogLevel, _ message: String, _ error: Error?) {
        lock.lock()
        defer { lock.unlock() }
        storage.append(error.map { "\(level.label): \(message) (\($0))" } ?? "\(level.label): \(message)")
    }
}
