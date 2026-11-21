import Foundation

/// Checks a profile before it can be used. Mirrors `ProfileValidator.cs` (same rules, same wording).
public enum ProfileValidator {
    static func isValidId(_ id: String) -> Bool {
        guard let first = id.first, first.isASCII, first.isLowercase || first.isNumber else { return false }
        return id.allSatisfy { $0.isASCII && ($0.isLowercase || $0.isNumber || $0 == "-") }
    }

    static func isValidManufacturer(_ text: String) -> Bool {
        text.count == 3 && text.allSatisfy { $0.isASCII && $0.isUppercase && $0.isLetter }
    }

    private static func isHexString(_ code: String) -> Bool {
        Hex.parseUInt16(code) != nil && code.lowercased().hasPrefix("0x")
    }

    public static func validate(_ profile: MonitorProfile) -> [String] {
        var errors: [String] = []

        if !isValidId(profile.id) {
            errors.append("id '\(profile.id)' must be lowercase letters, digits and dashes (e.g. lg-32un880).")
        }
        if profile.name.trimmingCharacters(in: .whitespaces).isEmpty {
            errors.append("name is required.")
        }

        let match = profile.match
        if !isValidManufacturer(match.manufacturer) {
            errors.append("match.manufacturer '\(match.manufacturer)' must be a three-letter EDID id such as GSM.")
        }

        let hasProduct = !(match.productCodes ?? []).isEmpty
        let hasCaps = !(match.capabilitiesModel?.trimmingCharacters(in: .whitespaces).isEmpty ?? true)
        let hasNames = !(match.modelNames ?? []).isEmpty
        if !hasProduct && !hasCaps && !hasNames {
            errors.append("match needs productCodes, capabilitiesModel or modelNames; a profile may not claim every monitor from a manufacturer.")
        }
        for code in match.productCodes ?? [] where !isHexString(code) {
            errors.append("match.productCodes entry '\(code)' must be a hex string like \"0x774F\".")
        }
        if hasNames && match.modelNames!.contains(where: { $0.trimmingCharacters(in: .whitespaces).isEmpty }) {
            errors.append("match.modelNames may not contain empty names.")
        }

        if ProtocolRegistry.find(profile.protocolId) == nil {
            errors.append("protocol '\(profile.protocolId)' is unknown. Known: \(ProtocolRegistry.all.map { $0.id }.joined(separator: ", ")).")
        }

        if profile.inputs.isEmpty {
            errors.append("inputs must list at least one input.")
        }
        var seen = Set<String>()
        for input in profile.inputs {
            if !isValidId(input.id) {
                errors.append("input id '\(input.id)' must be lowercase letters, digits and dashes (e.g. hdmi1, usbc).")
            } else if !seen.insert(input.id.lowercased()).inserted {
                errors.append("input id '\(input.id)' appears more than once.")
            }
            if input.name.trimmingCharacters(in: .whitespaces).isEmpty {
                errors.append("input '\(input.id)' needs a name.")
            }
            if !isHexString(input.code) {
                errors.append("input '\(input.id)' code '\(input.code)' must be a hex string like \"0xD1\".")
            }
        }

        if let q = profile.quirks {
            if !(0...5).contains(q.retries) {
                errors.append("quirks.retries must be between 0 and 5.")
            }
            if !(0...5000).contains(q.retryDelayMs) || !(0...5000).contains(q.postSwitchDelayMs) {
                errors.append("quirks delays must be between 0 and 5000 ms.")
            }
        }
        return errors
    }
}

/// How specifically a profile matched a monitor. Higher is better.
public enum MatchQuality: Int, Comparable {
    case none = 0
    /// No profile matched; a standard-MCCS profile was generated.
    case fallback = 1
    case modelName = 2
    case capabilitiesModel = 3
    case productCode = 4

    public static func < (lhs: MatchQuality, rhs: MatchQuality) -> Bool { lhs.rawValue < rhs.rawValue }
}

public struct ProfileMatchResult {
    public let profile: MonitorProfile
    public let quality: MatchQuality
}

/// Picks the best profile for a monitor. Mirrors `ProfileMatcher.cs`.
public enum ProfileMatcher {
    private static func same(_ a: String, _ b: String) -> Bool {
        a.trimmingCharacters(in: .whitespaces).caseInsensitiveCompare(b.trimmingCharacters(in: .whitespaces)) == .orderedSame
    }

    public static func evaluate(_ profile: MonitorProfile, _ monitor: MonitorIdentity, _ capabilities: MonitorCapabilities?) -> MatchQuality {
        let match = profile.match
        guard match.manufacturer.caseInsensitiveCompare(monitor.manufacturerId) == .orderedSame else { return .none }

        if match.productCodes?.contains(where: { Hex.parseUInt16($0) == monitor.productCode }) == true {
            return .productCode
        }
        if let wanted = match.capabilitiesModel, !wanted.trimmingCharacters(in: .whitespaces).isEmpty,
           let model = capabilities?.model, same(wanted, model) {
            return .capabilitiesModel
        }
        if let name = monitor.modelName, match.modelNames?.contains(where: { same($0, name) }) == true {
            return .modelName
        }
        return .none
    }

    /// Best match, or nil. Ties go to user profiles over built-ins, then to the alphabetically first id.
    public static func findBest(_ profiles: [MonitorProfile], _ monitor: MonitorIdentity, _ capabilities: MonitorCapabilities?) -> ProfileMatchResult? {
        profiles
            .map { ProfileMatchResult(profile: $0, quality: evaluate($0, monitor, capabilities)) }
            .filter { $0.quality > .fallback }
            .sorted { a, b in
                if a.quality != b.quality { return a.quality > b.quality }
                let au = a.profile.origin == .user, bu = b.profile.origin == .user
                if au != bu { return au }
                return a.profile.id < b.profile.id
            }
            .first
    }
}

public struct ProfileLoadError {
    public let source: String
    public let message: String
}

public struct ProfileFormatError: Error, CustomStringConvertible {
    public let source: String
    public let errors: [String]
    public var description: String { "\(source): \(errors.joined(separator: " "))" }
}

/// Built-in profiles (bundled) plus the user's profiles folder. Mirrors `ProfileCatalog.cs`.
public final class ProfileCatalog {
    public let profiles: [MonitorProfile]
    /// Profiles that failed to load. Shown by `--diagnose`; never fatal.
    public let errors: [ProfileLoadError]

    public init(profiles: [MonitorProfile], errors: [ProfileLoadError]) {
        self.profiles = profiles
        self.errors = errors
    }

    public static func load(userProfilesDirectory: URL?, log: LogSink) -> ProfileCatalog {
        var errors: [ProfileLoadError] = []
        var profiles = loadBuiltIn(errors: &errors)

        if let dir = userProfilesDirectory,
           let files = try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil) {
            let sorted = files.filter { $0.pathExtension.lowercased() == "json" }
                .sorted { $0.lastPathComponent.lowercased() < $1.lastPathComponent.lowercased() }
            for file in sorted {
                do {
                    let data = try Data(contentsOf: file)
                    profiles.append(try parse(data, origin: .user, source: file.path))
                } catch {
                    errors.append(ProfileLoadError(source: file.path, message: String(describing: error)))
                }
            }
        }

        for error in errors {
            log.warn("Profile not loaded: \(error.source): \(error.message)")
        }
        return ProfileCatalog(profiles: profiles, errors: errors)
    }

    public static func loadBuiltIn(errors: inout [ProfileLoadError]) -> [MonitorProfile] {
        guard let dir = CoreResources.profilesDirectory() else {
            errors.append(ProfileLoadError(source: "built-in profiles", message: "The bundled profiles folder was not found (run scripts/sync-shared.sh before building)."))
            return []
        }
        var files: [URL] = []
        if let enumerator = FileManager.default.enumerator(at: dir, includingPropertiesForKeys: nil) {
            for case let url as URL in enumerator
            where url.pathExtension.lowercased() == "json" && !url.lastPathComponent.hasSuffix(".schema.json") {
                files.append(url)
            }
        }
        var result: [MonitorProfile] = []
        for file in files.sorted(by: { $0.lastPathComponent < $1.lastPathComponent }) {
            do {
                result.append(try parse(try Data(contentsOf: file), origin: .builtIn, source: file.lastPathComponent))
            } catch {
                errors.append(ProfileLoadError(source: file.lastPathComponent, message: String(describing: error)))
            }
        }
        return result
    }

    /// Parses and validates one profile.
    public static func parse(_ data: Data, origin: ProfileOrigin, source: String) throws -> MonitorProfile {
        var profile: MonitorProfile
        do {
            profile = try JsonCoding.decoder().decode(MonitorProfile.self, from: data)
        } catch {
            throw ProfileFormatError(source: source, errors: [JsonCoding.describe(error)])
        }
        let problems = ProfileValidator.validate(profile)
        if !problems.isEmpty {
            throw ProfileFormatError(source: source, errors: problems)
        }
        profile.origin = origin
        profile.sourcePath = source
        return profile
    }

    public static func parse(_ json: String, origin: ProfileOrigin, source: String) throws -> MonitorProfile {
        try parse(Data(json.utf8), origin: origin, source: source)
    }

    public static func serialize(_ profile: MonitorProfile) -> String {
        guard let data = try? JsonCoding.encoder().encode(profile) else { return "{}" }
        return String(decoding: data, as: UTF8.self)
    }

    /// Finds a profile by id; a user profile wins over a built-in with the same id.
    public func findById(_ id: String) -> MonitorProfile? {
        let matches = profiles.filter { $0.id.caseInsensitiveCompare(id) == .orderedSame }
        return matches.first { $0.origin == .user } ?? matches.first
    }

    /// A forced profile id from settings, else the best match, else a generated standard-MCCS profile.
    public func resolve(_ monitor: MonitorIdentity, _ capabilities: MonitorCapabilities?, forcedProfileId: String? = nil) -> ProfileMatchResult {
        if let forcedProfileId, let forced = findById(forcedProfileId) {
            return ProfileMatchResult(profile: forced, quality: .productCode)
        }
        return ProfileMatcher.findBest(profiles, monitor, capabilities)
            ?? ProfileMatchResult(profile: ProfileCatalog.createFallback(monitor, capabilities), quality: .fallback)
    }

    /// Standard MCCS profile for an unknown monitor. Every input is unverified.
    public static func createFallback(_ monitor: MonitorIdentity, _ capabilities: MonitorCapabilities?) -> MonitorProfile {
        var seen = Set<String>()
        let inputs = StandardMccsProtocol().candidates(for: capabilities)
            .filter { seen.insert($0.inputId).inserted }
            .map { ProfileInput(id: $0.inputId, name: $0.name, code: Hex.format($0.code)) }

        var profile = MonitorProfile()
        profile.id = "auto-\(monitor.manufacturerId.lowercased())-\(Hex.pad(monitor.productCode, 4).lowercased())"
        profile.name = monitor.modelName ?? "\(monitor.manufacturerId) monitor 0x\(Hex.pad(monitor.productCode, 4))"
        profile.match = ProfileMatch(manufacturer: monitor.manufacturerId, productCodes: ["0x\(Hex.pad(monitor.productCode, 4))"])
        profile.protocolId = StandardMccsProtocol.protocolId
        profile.inputs = inputs
        profile.origin = .fallback
        profile.notes = "Generated automatically. Run the setup wizard to confirm which codes work."
        return profile
    }
}

/// Finds the bundled profiles without `Bundle.module`, whose generated accessor calls fatalError
/// when the SwiftPM resource bundle isn't at the build path (e.g. inside a copied .app).
public enum CoreResources {
    static let bundleName = "MonitorSwitcher_MonitorSwitcherCore.bundle"

    private final class Marker {}

    public static func profilesDirectory() -> URL? {
        let fm = FileManager.default
        var isDir: ObjCBool = false

        if let override = ProcessInfo.processInfo.environment["MONITORSWITCHER_PROFILES_DIR"],
           fm.fileExists(atPath: override, isDirectory: &isDir), isDir.boolValue {
            return URL(fileURLWithPath: override)
        }

        // Inside the .app, build-app.sh copies the profiles to Contents/Resources/profiles. The CLI
        // lives in Contents/MacOS, so also look relative to the executable.
        let appResources: [URL?] = [
            Bundle.main.resourceURL?.appendingPathComponent("profiles"),
            Bundle.main.executableURL?.deletingLastPathComponent().deletingLastPathComponent()
                .appendingPathComponent("Resources/profiles"),
        ]
        for candidate in appResources.compactMap({ $0 }) where fm.fileExists(atPath: candidate.path, isDirectory: &isDir) && isDir.boolValue {
            return candidate
        }

        let markerBundle = Bundle(for: Marker.self)
        let roots: [URL?] = [
            Bundle.main.resourceURL,                                  // inside the .app: Contents/Resources
            Bundle.main.bundleURL,
            Bundle.main.executableURL?.deletingLastPathComponent(),   // swift run, CLI next to the bundle
            markerBundle.bundleURL.deletingLastPathComponent(),       // swift test: next to the .xctest
            markerBundle.resourceURL,
        ]
        for root in roots.compactMap({ $0 }) {
            let bundleURL = root.appendingPathComponent(bundleName)
            guard fm.fileExists(atPath: bundleURL.path) else { continue }
            let candidates = [
                Bundle(url: bundleURL)?.url(forResource: "profiles", withExtension: nil),
                bundleURL.appendingPathComponent("profiles"),
                bundleURL.appendingPathComponent("Contents/Resources/profiles"),
            ]
            for candidate in candidates.compactMap({ $0 }) where fm.fileExists(atPath: candidate.path, isDirectory: &isDir) && isDir.boolValue {
                return candidate
            }
        }

        // Developer fallback: the synced source folder.
        let source = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()   // Profiles
            .deletingLastPathComponent()   // MonitorSwitcherCore
            .appendingPathComponent("Resources/profiles")
        if fm.fileExists(atPath: source.path, isDirectory: &isDir), isDir.boolValue {
            return source
        }
        return nil
    }
}
