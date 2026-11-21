import Foundation

public struct PortChoice: Hashable, Identifiable {
    public let inputId: String
    public let name: String
    public var id: String { inputId }
}

/// One thing the wizard will try: send `target`, then switch back with `back`.
public struct SetupAttempt {
    public let inputProtocol: any InputProtocol
    public let target: InputCandidate
    public let back: InputCandidate?
}

/// An input the wizard tested, ready to be written into a profile.
public struct ConfirmedInput {
    public let inputId: String
    public let name: String
    public let code: UInt16
    public let verified: Bool

    public init(_ inputId: String, _ name: String, _ code: UInt16, verified: Bool) {
        self.inputId = inputId
        self.name = name
        self.code = code
        self.verified = verified
    }
}

/// Decides what the setup wizard tries and in which order. Mirrors `SetupPlanner.cs`. Codes only
/// ever come from the monitor's profile or from a protocol's own candidate list.
public enum SetupPlanner {
    private static let canonicalOrder = ["hdmi1", "hdmi2", "hdmi3", "dp1", "dp2", "usbc", "usbc2", "dvi1", "dvi2", "vga1"]

    /// Ports to offer in the wizard's "which input" lists.
    public static func portChoices(_ monitor: ResolvedMonitor) -> [PortChoice] {
        if !monitor.needsSetup {
            return monitor.profile.inputs.map { PortChoice(inputId: $0.id, name: $0.name) }
        }
        var seen = Set<String>()
        var choices: [PortChoice] = []
        for candidate in usableProtocols(monitor).flatMap({ $0.candidates(for: monitor.capabilities) })
        where seen.insert(candidate.inputId.lowercased()).inserted {
            choices.append(PortChoice(inputId: candidate.inputId, name: candidate.name))
        }
        func order(_ c: PortChoice) -> Int { canonicalOrder.firstIndex(of: c.inputId) ?? Int.max }
        return choices.sorted { order($0) != order($1) ? order($0) < order($1) : $0.inputId < $1.inputId }
    }

    /// The profile's protocol first (if it has one), then protocols ranked for this monitor,
    /// limited to ones a transport can send.
    public static func usableProtocols(_ monitor: ResolvedMonitor) -> [any InputProtocol] {
        var ordered: [any InputProtocol] = []
        if !monitor.needsSetup {
            ordered.append(monitor.inputProtocol)
        }
        for p in ProtocolRegistry.ranked(for: monitor.identity, capabilities: monitor.capabilities) where !ordered.contains(where: { $0.id == p.id }) {
            ordered.append(p)
        }
        return ordered.filter { monitor.endpoint(for: $0) != nil }
    }

    /// Candidates for a protocol: the profile's own codes first (when it uses this protocol), then the protocol's list.
    public static func candidates(_ monitor: ResolvedMonitor, _ inputProtocol: any InputProtocol) -> [InputCandidate] {
        var result: [InputCandidate] = []
        if !monitor.needsSetup && monitor.profile.protocolId.caseInsensitiveCompare(inputProtocol.id) == .orderedSame {
            result.append(contentsOf: monitor.profile.inputs.map { InputCandidate($0.id, $0.name, $0.codeValue) })
        }
        for candidate in inputProtocol.candidates(for: monitor.capabilities)
        where !result.contains(where: { $0.inputId.caseInsensitiveCompare(candidate.inputId) == .orderedSame && $0.code == candidate.code }) {
            result.append(candidate)
        }
        return result
    }

    /// Every attempt for switching from `localInputId` to `targetInputId`.
    public static func plan(_ monitor: ResolvedMonitor, localInputId: String, targetInputId: String) -> [SetupAttempt] {
        var attempts: [SetupAttempt] = []
        for p in usableProtocols(monitor) {
            let list = candidates(monitor, p)
            let back = list.first { $0.inputId.caseInsensitiveCompare(localInputId) == .orderedSame }
            for target in list where target.inputId.caseInsensitiveCompare(targetInputId) == .orderedSame {
                attempts.append(SetupAttempt(inputProtocol: p, target: target, back: back))
            }
        }
        return attempts
    }

    /// True when the results differ from the profile already in use, so a user profile is needed.
    public static func needsUserProfile(_ monitor: ResolvedMonitor, _ inputProtocol: any InputProtocol, _ confirmed: [ConfirmedInput], ignoresCommandsWhenInactive: Bool?) -> Bool {
        if monitor.needsSetup || monitor.profile.protocolId.caseInsensitiveCompare(inputProtocol.id) != .orderedSame {
            return true
        }
        if let quirk = ignoresCommandsWhenInactive, quirk != monitor.profile.effectiveQuirks.ignoresCommandsWhenInactive {
            return true
        }
        return confirmed.contains { c in
            guard let existing = monitor.profile.findInput(c.inputId) else { return true }
            return existing.codeValue != c.code
        }
    }

    /// Builds (or extends) the user's profile for this monitor. It matches on manufacturer and
    /// product code only, so it never claims other models.
    public static func buildUserProfile(_ monitor: ResolvedMonitor, _ inputProtocol: any InputProtocol, _ confirmed: [ConfirmedInput],
                                        ignoresCommandsWhenInactive: Bool?, existingUserProfile: MonitorProfile?) -> MonitorProfile {
        let identity = monitor.identity
        let base: MonitorProfile?
        if let existing = existingUserProfile, existing.protocolId.caseInsensitiveCompare(inputProtocol.id) == .orderedSame {
            base = existing
        } else if !monitor.needsSetup && monitor.profile.protocolId.caseInsensitiveCompare(inputProtocol.id) == .orderedSame {
            base = monitor.profile
        } else {
            base = nil
        }

        var inputs = base?.inputs.map { ProfileInput(id: $0.id, name: $0.name, code: $0.code, verified: $0.verified) } ?? []
        for c in confirmed {
            if let index = inputs.firstIndex(where: { $0.id.caseInsensitiveCompare(c.inputId) == .orderedSame }) {
                let previouslyVerifiedSameCode = inputs[index].verified && inputs[index].codeValue == c.code
                inputs[index].code = Hex.format(c.code)
                inputs[index].verified = c.verified || previouslyVerifiedSameCode
            } else {
                inputs.append(ProfileInput(id: c.inputId, name: c.name, code: Hex.format(c.code), verified: c.verified))
            }
        }

        let baseQuirks = base?.effectiveQuirks ?? ProfileQuirks()
        var quirks = ProfileQuirks()
        quirks.retries = baseQuirks.retries
        quirks.retryDelayMs = baseQuirks.retryDelayMs
        quirks.postSwitchDelayMs = baseQuirks.postSwitchDelayMs
        quirks.ignoresCommandsWhenInactive = ignoresCommandsWhenInactive ?? baseQuirks.ignoresCommandsWhenInactive

        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd"

        var profile = MonitorProfile()
        profile.schema = "../profile.schema.json"
        profile.id = existingUserProfile?.id ?? "\(identity.manufacturerId.lowercased())-\(Hex.pad(identity.productCode, 4).lowercased())"
        profile.name = monitor.needsSetup
            ? (identity.modelName ?? "\(identity.manufacturerId) 0x\(Hex.pad(identity.productCode, 4))")
            : monitor.profile.name
        profile.match = ProfileMatch(manufacturer: identity.manufacturerId, productCodes: ["0x\(Hex.pad(identity.productCode, 4))"])
        profile.protocolId = inputProtocol.id
        profile.inputs = inputs
        profile.quirks = quirks
        profile.notes = "Created by the Monitor Switcher setup wizard on \(formatter.string(from: Date())) for \(identity)."
        profile.origin = .user
        return profile
    }
}
