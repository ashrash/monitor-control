import Darwin
import Foundation
import MonitorSwitcherCore

/// Cross-process lock using flock(2) on a lock file. flock locks belong to the open file, so two
/// threads (or processes) that open the file separately exclude each other. Not re-entrant.
public final class FileLock: CrossProcessLock {
    private let path: String

    public init(path: URL) {
        self.path = path.path
    }

    public func withLock<T>(timeout: TimeInterval, _ body: () throws -> T) throws -> T {
        try FileManager.default.createDirectory(at: URL(fileURLWithPath: path).deletingLastPathComponent(), withIntermediateDirectories: true)
        let fd = open(path, O_CREAT | O_RDWR, 0o644)
        guard fd >= 0 else { throw LockError.cannotOpen(path) }
        defer { close(fd) }

        let deadline = Date().addingTimeInterval(timeout)
        while flock(fd, LOCK_EX | LOCK_NB) != 0 {
            guard errno == EWOULDBLOCK, Date() < deadline else { throw LockError.timeout(path) }
            usleep(10_000)
        }
        defer { flock(fd, LOCK_UN) }
        return try body()
    }
}

/// Appends to logs/monitorswitcher.log and rotates it at 1 MB, keeping 5 files. Safe from several
/// processes at once. Logging never throws.
public final class FileLog: LogSink {
    private let directory: URL
    private let file: URL
    private let tag: String
    private let minimum: LogLevel
    private let maxBytes: Int
    private let keep: Int
    private let lock: FileLock
    private let formatter: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyy-MM-dd HH:mm:ss.SSS ZZZZZ"
        return f
    }()

    public init(directory: URL, processTag: String, minimum: LogLevel = .info, maxBytes: Int = 1_000_000, keep: Int = 5) {
        self.directory = directory
        self.file = directory.appendingPathComponent("monitorswitcher.log")
        self.tag = processTag
        self.minimum = minimum
        self.maxBytes = maxBytes
        self.keep = keep
        self.lock = FileLock(path: directory.appendingPathComponent(".log.lock"))
    }

    public var filePath: URL { file }

    public func write(_ level: LogLevel, _ message: String, _ error: Error?) {
        guard level >= minimum else { return }
        var line = "\(formatter.string(from: Date())) \(level.label.padding(toLength: 5, withPad: " ", startingAt: 0)) [\(tag):\(getpid())] \(message)"
        if let error {
            line += "\n    \(error)"
        }
        line += "\n"

        _ = try? lock.withLock(timeout: 2) {
            let size = ((try? FileManager.default.attributesOfItem(atPath: file.path))?[.size] as? NSNumber)?.intValue ?? 0
            if size + line.utf8.count > maxBytes {
                rotate()
            }
            append(line)
        }
    }

    private func append(_ line: String) {
        let data = Data(line.utf8)
        if !FileManager.default.fileExists(atPath: file.path) {
            FileManager.default.createFile(atPath: file.path, contents: data)
            return
        }
        guard let handle = try? FileHandle(forWritingTo: file) else { return }
        defer { try? handle.close() }
        _ = try? handle.seekToEnd()
        try? handle.write(contentsOf: data)
    }

    private func rotate() {
        let fm = FileManager.default
        try? fm.removeItem(atPath: "\(file.path).\(keep - 1)")
        if keep > 2 {
            for i in stride(from: keep - 2, through: 1, by: -1) {
                let from = "\(file.path).\(i)"
                if fm.fileExists(atPath: from) {
                    try? fm.moveItem(atPath: from, toPath: "\(file.path).\(i + 1)")
                }
            }
        }
        try? fm.moveItem(atPath: file.path, toPath: "\(file.path).1")
    }
}

/// Composition root for macOS: builds the transport, stores and the switch service.
public enum MacPlatform {
    /// ~/Library/Application Support/MonitorSwitcher, or MONITORSWITCHER_HOME if set.
    public static func defaultPaths() -> AppPaths {
        if let home = ProcessInfo.processInfo.environment["MONITORSWITCHER_HOME"], !home.isEmpty {
            return AppPaths(root: URL(fileURLWithPath: home, isDirectory: true))
        }
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Application Support")
        return AppPaths(root: support.appendingPathComponent("MonitorSwitcher", isDirectory: true))
    }

    /// - Parameters:
    ///   - processTag: written on each log line, e.g. "app" or "cli".
    ///   - extraLog: optional second sink, e.g. stderr for `--verbose`.
    public static func create(processTag: String, extraLog: LogSink? = nil, fileLogLevel: LogLevel = .info) -> AppServices {
        let paths = defaultPaths()
        try? FileManager.default.createDirectory(at: paths.root, withIntermediateDirectories: true)
        let fileLog = FileLog(directory: paths.logsDirectory, processTag: processTag, minimum: fileLogLevel)
        let log: LogSink = extraLog.map { TeeLog([fileLog, $0]) } ?? fileLog

        let transports: [DdcTransport] = [IOAVServiceTransport(log: log)]
        let settings = JsonFileStore<AppSettings>(path: paths.settingsFile, lock: FileLock(path: paths.root.appendingPathComponent(".settings.lock")),
                                                  log: log, makeDefault: { AppSettings() })
        let state = JsonFileStore<AppState>(path: paths.stateFile, lock: FileLock(path: paths.root.appendingPathComponent(".state.lock")),
                                            log: log, makeDefault: { AppState() })
        let catalog = ProfileCatalog.load(userProfilesDirectory: paths.userProfilesDirectory, log: log)
        let switcher = SwitchService(transports: transports, catalog: catalog, stateStore: state,
                                     busLock: FileLock(path: paths.root.appendingPathComponent(".ddcbus.lock")), log: log)
        return AppServices(paths: paths, log: log, transports: transports, settings: settings, state: state, switcher: switcher)
    }
}
