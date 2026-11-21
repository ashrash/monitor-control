import Foundation

/// Shared JSON settings for every file the app reads or writes.
public enum JsonCoding {
    public static func encoder() -> JSONEncoder {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        encoder.dateEncodingStrategy = .iso8601
        return encoder
    }

    /// Accepts comments and trailing commas (JSON5), like the Windows reader.
    public static func decoder() -> JSONDecoder {
        let decoder = JSONDecoder()
        decoder.allowsJSON5 = true
        decoder.dateDecodingStrategy = .iso8601
        return decoder
    }

    /// One readable sentence for a decoding error.
    public static func describe(_ error: Error) -> String {
        switch error {
        case DecodingError.dataCorrupted(let context):
            return context.debugDescription
        case DecodingError.keyNotFound(let key, _):
            return "Missing property '\(key.stringValue)'."
        case DecodingError.typeMismatch(let type, let context):
            return "Wrong type at \(context.codingPath.map(\.stringValue).joined(separator: ".")): expected \(type)."
        case DecodingError.valueNotFound(let type, let context):
            return "Missing value at \(context.codingPath.map(\.stringValue).joined(separator: ".")): expected \(type)."
        default:
            return error.localizedDescription
        }
    }
}

/// Where the app keeps its files. The platform layer chooses the root folder.
public struct AppPaths {
    public let root: URL
    public init(root: URL) { self.root = root }
    public var settingsFile: URL { root.appendingPathComponent("settings.json") }
    public var stateFile: URL { root.appendingPathComponent("state.json") }
    /// User profiles. Same format as the built-in ones; a user profile wins a tie.
    public var userProfilesDirectory: URL { root.appendingPathComponent("profiles", isDirectory: true) }
    public var logsDirectory: URL { root.appendingPathComponent("logs", isDirectory: true) }
}

public enum LockError: Error, CustomStringConvertible {
    case timeout(String)
    case cannotOpen(String)

    public var description: String {
        switch self {
        case .timeout(let name): return "Another Monitor Switcher process is holding '\(name)'."
        case .cannotOpen(let name): return "Could not open lock file '\(name)'."
        }
    }
}

/// A lock shared by every Monitor Switcher process (menu bar app and CLI). Not re-entrant.
public protocol CrossProcessLock: AnyObject {
    func withLock<T>(timeout: TimeInterval, _ body: () throws -> T) throws -> T
}

/// In-process lock, for tests and single-process tools.
public final class InProcessLock: CrossProcessLock {
    private let lock = NSLock()
    public init() {}

    public func withLock<T>(timeout: TimeInterval, _ body: () throws -> T) throws -> T {
        guard lock.lock(before: Date().addingTimeInterval(timeout)) else { throw LockError.timeout("in-process lock") }
        defer { lock.unlock() }
        return try body()
    }
}

/// Loads and saves one JSON file. Writes go to a temp file that is then renamed into place, so a
/// crash never leaves a half-written file. A corrupt file is kept as *.bad and replaced with
/// defaults. Mirrors `JsonFileStore.cs`.
public final class JsonFileStore<T: Codable> {
    public let path: URL
    private let lock: CrossProcessLock
    private let log: LogSink
    private let makeDefault: () -> T
    private let timeout: TimeInterval = 5

    public init(path: URL, lock: CrossProcessLock, log: LogSink, makeDefault: @escaping () -> T) {
        self.path = path
        self.lock = lock
        self.log = log
        self.makeDefault = makeDefault
    }

    public func load() -> T {
        (try? lock.withLock(timeout: timeout) { loadUnlocked() }) ?? loadUnlocked()
    }

    public func save(_ value: T) throws {
        try lock.withLock(timeout: timeout) { try saveUnlocked(value) }
    }

    /// Load, change and save as one step, so concurrent processes don't overwrite each other.
    @discardableResult
    public func update(_ mutate: (inout T) throws -> Void) throws -> T {
        try lock.withLock(timeout: timeout) {
            var value = loadUnlocked()
            try mutate(&value)
            try saveUnlocked(value)
            return value
        }
    }

    private func loadUnlocked() -> T {
        guard let data = FileManager.default.contents(atPath: path.path) else { return makeDefault() }
        do {
            return try JsonCoding.decoder().decode(T.self, from: data)
        } catch {
            let backup = path.appendingPathExtension("bad")
            log.warn("\(path.path) could not be read and was reset to defaults; the old file is at \(backup.path).", error)
            try? FileManager.default.removeItem(at: backup)
            try? FileManager.default.copyItem(at: path, to: backup)
            return makeDefault()
        }
    }

    private func saveUnlocked(_ value: T) throws {
        try FileManager.default.createDirectory(at: path.deletingLastPathComponent(), withIntermediateDirectories: true)
        let data = try JsonCoding.encoder().encode(value)
        let temp = path.appendingPathExtension("tmp")
        try data.write(to: temp)
        guard rename(temp.path, path.path) == 0 else {
            throw CocoaError(.fileWriteUnknown, userInfo: [NSFilePathErrorKey: path.path])
        }
    }
}

/// User preferences (settings.json). Mirrors `AppSettings.cs`.
public final class AppSettings: Codable {
    public var version: Int = 1
    /// Monitor used when the CLI gets no --monitor and several monitors are connected.
    public var defaultMonitorId: String?
    public var monitors: [MonitorSettings] = []
    public var hotkeys: [HotkeyBinding] = []
    public var notifyOnSwitch: Bool = false

    enum CodingKeys: String, CodingKey {
        case version, defaultMonitorId, monitors, hotkeys, notifyOnSwitch
    }

    public init() {}

    public required init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        version = try c.decodeIfPresent(Int.self, forKey: .version) ?? 1
        defaultMonitorId = try c.decodeIfPresent(String.self, forKey: .defaultMonitorId)
        monitors = try c.decodeIfPresent([MonitorSettings].self, forKey: .monitors) ?? []
        hotkeys = try c.decodeIfPresent([HotkeyBinding].self, forKey: .hotkeys) ?? []
        notifyOnSwitch = try c.decodeIfPresent(Bool.self, forKey: .notifyOnSwitch) ?? false
    }

    public func findMonitor(_ monitorId: String?) -> MonitorSettings? {
        guard let monitorId else { return nil }
        return monitors.first { $0.monitorId.caseInsensitiveCompare(monitorId) == .orderedSame }
    }

    public func getOrAddMonitor(_ monitorId: String) -> MonitorSettings {
        if let existing = findMonitor(monitorId) { return existing }
        let created = MonitorSettings(monitorId: monitorId)
        monitors.append(created)
        return created
    }

    /// Deep copy (for editing in a window without touching the live settings).
    public func copy() -> AppSettings {
        guard let data = try? JsonCoding.encoder().encode(self),
              let copy = try? JsonCoding.decoder().decode(AppSettings.self, from: data) else { return AppSettings() }
        return copy
    }
}

public final class MonitorSettings: Codable {
    public var monitorId: String = ""
    /// Optional friendly name for the monitor itself.
    public var label: String?
    /// Force a specific profile id instead of automatic matching.
    public var profileId: String?
    /// The input this computer is plugged into (used by toggle and by the setup wizard).
    public var localInputId: String?
    /// The two inputs the toggle hotkey switches between.
    public var toggleInputs: [String]?
    /// Friendly names per input id, e.g. ["usbc": "Mac", "hdmi2": "PC"].
    public var inputLabels: [String: String] = [:]

    enum CodingKeys: String, CodingKey {
        case monitorId, label, profileId, localInputId, toggleInputs, inputLabels
    }

    public init(monitorId: String) {
        self.monitorId = monitorId
    }

    public required init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        monitorId = try c.decodeIfPresent(String.self, forKey: .monitorId) ?? ""
        label = try c.decodeIfPresent(String.self, forKey: .label)
        profileId = try c.decodeIfPresent(String.self, forKey: .profileId)
        localInputId = try c.decodeIfPresent(String.self, forKey: .localInputId)
        toggleInputs = try c.decodeIfPresent([String].self, forKey: .toggleInputs)
        inputLabels = try c.decodeIfPresent([String: String].self, forKey: .inputLabels) ?? [:]
    }

    public func labelFor(_ inputId: String) -> String? {
        inputLabels.first { $0.key.caseInsensitiveCompare(inputId) == .orderedSame && !$0.value.isEmpty }?.value
    }
}

public enum HotkeyAction: String, Codable {
    case switchInput = "SwitchInput"
    case toggle = "Toggle"
}

public struct HotkeyBinding: Codable, Equatable {
    public var monitorId: String
    public var action: HotkeyAction
    /// Input id for `.switchInput`.
    public var inputId: String?
    /// Key combination such as "Ctrl+Option+1". Parsed by the app.
    public var keys: String

    public init(monitorId: String, action: HotkeyAction, inputId: String? = nil, keys: String) {
        self.monitorId = monitorId
        self.action = action
        self.inputId = inputId
        self.keys = keys
    }
}

/// Machine state the app learns as it runs (state.json), shared by the app and the CLI.
public final class AppState: Codable {
    public var monitors: [String: MonitorState] = [:]

    enum CodingKeys: String, CodingKey { case monitors }

    public init() {}

    public required init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        monitors = try c.decodeIfPresent([String: MonitorState].self, forKey: .monitors) ?? [:]
    }

    public func getOrAdd(_ monitorId: String) -> MonitorState {
        if let existing = monitors[monitorId] { return existing }
        let created = MonitorState()
        monitors[monitorId] = created
        return created
    }
}

public final class MonitorState: Codable {
    /// Last input this app sent. Monitors such as the 32UN880 cannot report their current input.
    public var lastInputId: String?
    public var lastSwitchedAt: Date?
    /// Where the monitor was last reached.
    public var lastEndpoint: EndpointRecord?
    public var identity: MonitorIdentity?
    /// Cached DDC/CI capabilities string.
    public var capabilities: String?

    enum CodingKeys: String, CodingKey {
        case lastInputId, lastSwitchedAt, lastEndpoint, identity, capabilities
    }

    public init() {}

    public required init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        lastInputId = try c.decodeIfPresent(String.self, forKey: .lastInputId)
        lastSwitchedAt = try? c.decodeIfPresent(Date.self, forKey: .lastSwitchedAt)
        lastEndpoint = try c.decodeIfPresent(EndpointRecord.self, forKey: .lastEndpoint)
        identity = try c.decodeIfPresent(MonitorIdentity.self, forKey: .identity)
        capabilities = try c.decodeIfPresent(String.self, forKey: .capabilities)
    }
}

/// Transport-specific address, e.g. transport "ioavservice", address "0x1000005f3".
public struct EndpointRecord: Codable, Equatable {
    public var transport: String
    public var address: String

    public init(transport: String, address: String) {
        self.transport = transport
        self.address = address
    }
}
