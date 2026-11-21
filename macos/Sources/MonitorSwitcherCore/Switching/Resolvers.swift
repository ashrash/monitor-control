import Foundation

/// A connected monitor with its profile and every transport that can reach it. Mirrors `ResolvedMonitor.cs`.
public final class ResolvedMonitor {
    public let identity: MonitorIdentity
    /// Endpoints ordered by transport preference.
    public let endpoints: [DisplayEndpoint]
    public let profile: MonitorProfile
    public let quality: MatchQuality
    public let capabilities: MonitorCapabilities?

    public init(identity: MonitorIdentity, endpoints: [DisplayEndpoint], profile: MonitorProfile, quality: MatchQuality, capabilities: MonitorCapabilities?) {
        self.identity = identity
        self.endpoints = endpoints
        self.profile = profile
        self.quality = quality
        self.capabilities = capabilities
    }

    public var stableId: String { identity.stableId }

    public var name: String {
        profile.origin == .fallback ? (identity.modelName ?? profile.name) : profile.name
    }

    public var osDisplayName: String? { endpoints.lazy.compactMap { $0.osDisplayName }.first }

    /// True when the OS no longer lists the monitor (for example while it shows another computer).
    public var isRestoredFromCache: Bool { endpoints.allSatisfy { $0.isRestoredFromCache } }

    /// True when no profile matched and the setup wizard should be offered.
    public var needsSetup: Bool { profile.origin == .fallback }

    /// Profiles are validated, so the protocol always exists; standard MCCS is a defensive fallback.
    public var inputProtocol: any InputProtocol { ProtocolRegistry.find(profile.protocolId) ?? ProtocolRegistry.standard }

    /// The preferred endpoint whose transport can deliver the protocol's frames.
    public func endpoint(for inputProtocol: any InputProtocol) -> DisplayEndpoint? {
        endpoints.first { $0.transport.canSend(inputProtocol) }
    }
}

public struct SwitchOutcome {
    public let monitor: ResolvedMonitor
    public let inputId: String
    public let inputName: String
    public let code: UInt16
    public let endpoint: DisplayEndpoint
    public let attempts: Int
    public let elapsed: TimeInterval
}

private func eq(_ a: String?, _ b: String) -> Bool {
    guard let a else { return false }
    return a.caseInsensitiveCompare(b) == .orderedSame
}

private func startsWith(_ a: String?, _ b: String) -> Bool {
    guard let a else { return false }
    return a.lowercased().hasPrefix(b.lowercased())
}

private func containsText(_ a: String?, _ b: String) -> Bool {
    guard let a else { return false }
    return a.localizedCaseInsensitiveContains(b)
}

/// Turns what a person types ("usbc", "USB-C", "Mac", "us") into a profile input. Mirrors `InputResolver`.
public enum InputResolver {
    public static func resolve(_ profile: MonitorProfile, _ settings: MonitorSettings?, _ query: String) throws -> ProfileInput {
        let q = query.trimmingCharacters(in: .whitespaces)
        let inputs = profile.inputs

        if let exact = inputs.first(where: { eq($0.id, q) })
            ?? inputs.first(where: { eq($0.name, q) })
            ?? inputs.first(where: { eq(settings?.labelFor($0.id), q) }) {
            return exact
        }

        let prefix = inputs.filter { startsWith($0.id, q) || startsWith($0.name, q) || startsWith(settings?.labelFor($0.id), q) }
        if prefix.count == 1 {
            return prefix[0]
        }

        throw DdcError(
            .inputNotInProfile,
            prefix.count > 1
                ? "'\(query)' matches more than one input of \(profile.name): \(describe(prefix, settings))."
                : "'\(query)' is not an input of \(profile.name). Choose one of: \(describe(inputs, settings)).")
    }

    public static func describe(_ inputs: [ProfileInput], _ settings: MonitorSettings?) -> String {
        inputs.map { input in
            if let label = settings?.labelFor(input.id) {
                return "\(input.id) (\(input.name), \"\(label)\")"
            }
            return "\(input.id) (\(input.name))"
        }.joined(separator: ", ")
    }
}

/// Picks exactly one monitor. Never broadcasts: ambiguity is an error. Mirrors `MonitorResolver`.
public enum MonitorResolver {
    public static func resolve(_ monitors: [ResolvedMonitor], _ settings: AppSettings, _ query: String?) throws -> ResolvedMonitor {
        if monitors.isEmpty {
            throw DdcError(.monitorNotFound, "No monitor that supports DDC/CI was found.", hint: DdcHints.ddcCiDisabled)
        }

        guard let raw = query, !raw.trimmingCharacters(in: .whitespaces).isEmpty else {
            if let defaultId = settings.defaultMonitorId, let preferred = monitors.first(where: { $0.stableId == defaultId }) {
                return preferred
            }
            if monitors.count == 1 {
                return monitors[0]
            }
            throw ambiguous(monitors, settings, "Several monitors are connected.")
        }

        let q = raw.trimmingCharacters(in: .whitespaces)
        if let index = Int(q), index >= 1, index <= monitors.count {
            return monitors[index - 1]
        }

        let exact = monitors.filter { m in
            eq(m.stableId, q)
                || eq(m.osDisplayName, q)
                || eq(settings.findMonitor(m.stableId)?.label, q)
                || eq(m.name, q)
                || eq(m.identity.modelName, q)
        }
        if exact.count == 1 {
            return exact[0]
        }

        let partial = monitors.filter { m in
            containsText(m.name, q) || containsText(m.identity.modelName, q) || containsText(settings.findMonitor(m.stableId)?.label, q)
        }
        if exact.isEmpty && partial.count == 1 {
            return partial[0]
        }

        if exact.count > 1 || partial.count > 1 {
            throw ambiguous(monitors, settings, "'\(raw)' matches more than one monitor.")
        }
        throw DdcError(.monitorNotFound, "No connected monitor matches '\(raw)'. Connected: \(list(monitors, settings)).")
    }

    private static func ambiguous(_ monitors: [ResolvedMonitor], _ settings: AppSettings, _ reason: String) -> DdcError {
        DdcError(.ambiguousMonitor, "\(reason) Choose one with --monitor <number or name>: \(list(monitors, settings)).",
                 hint: "Or pick a default monitor in Settings.")
    }

    private static func list(_ monitors: [ResolvedMonitor], _ settings: AppSettings) -> String {
        monitors.enumerated().map { i, m in
            "\(i + 1)) \(settings.findMonitor(m.stableId)?.label ?? m.name) [\(m.stableId)]"
        }.joined(separator: "; ")
    }
}

/// Chooses the toggle target. The monitor can't report its input, so we use what we last sent.
/// Mirrors `ToggleLogic`.
public enum ToggleLogic {
    /// From settings, else the profile's two verified inputs, else a two-input profile.
    public static func toggleInputs(_ profile: MonitorProfile, _ settings: MonitorSettings?) -> (a: String, b: String)? {
        if let pair = settings?.toggleInputs, pair.count == 2,
           profile.findInput(pair[0]) != nil, profile.findInput(pair[1]) != nil,
           pair[0].caseInsensitiveCompare(pair[1]) != .orderedSame {
            return (pair[0], pair[1])
        }
        let verified = profile.inputs.filter { $0.verified }
        if verified.count == 2 {
            return (verified[0].id, verified[1].id)
        }
        if profile.inputs.count == 2 {
            return (profile.inputs[0].id, profile.inputs[1].id)
        }
        return nil
    }

    /// Last sent was A → B, and vice versa. With no history, assume the monitor shows this computer
    /// and go to the other input; if even that is unknown, go to B. If the monitor only obeys the
    /// computer it is showing, any command this computer can deliver must be "go elsewhere".
    public static func nextInput(_ a: String, _ b: String, lastInputId: String?, localInputId: String?, ignoresCommandsWhenInactive: Bool = false) -> String {
        if ignoresCommandsWhenInactive && eq(localInputId, a) { return b }
        if ignoresCommandsWhenInactive && eq(localInputId, b) { return a }
        if eq(lastInputId, a) { return b }
        if eq(lastInputId, b) { return a }
        if eq(localInputId, b) { return a }
        return b
    }
}
