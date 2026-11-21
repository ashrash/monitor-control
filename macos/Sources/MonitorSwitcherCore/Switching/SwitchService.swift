import Foundation

public struct TransportReport {
    public let transport: DdcTransport
    public let status: TransportStatus
}

/// The one entry point UIs use to switch inputs. Mirrors `SwitchService.cs`:
/// profile (which codes are allowed) → protocol (which bytes) → transport (how to deliver).
/// Every value is checked against the monitor's profile, or for the setup wizard against the
/// protocol's candidate list, before anything is sent.
public final class SwitchService {
    private static let busTimeout: TimeInterval = 5

    private let transports: [DdcTransport]
    public private(set) var catalog: ProfileCatalog
    public let state: JsonFileStore<AppState>
    private let busLock: CrossProcessLock
    private let log: LogSink
    private var reports: [TransportReport]?
    private let reportsLock = NSLock()

    public init(transports: [DdcTransport], catalog: ProfileCatalog, stateStore: JsonFileStore<AppState>, busLock: CrossProcessLock, log: LogSink) {
        self.transports = transports
        self.catalog = catalog
        self.state = stateStore
        self.busLock = busLock
        self.log = log
    }

    /// Swaps in a freshly loaded catalog, e.g. after the setup wizard saves a user profile.
    public func reloadProfiles(_ updated: ProfileCatalog) {
        catalog = updated
    }

    /// Availability of every transport. Cached; pass `refresh` after driver changes.
    public func probeTransports(refresh: Bool = false) -> [TransportReport] {
        reportsLock.lock()
        defer { reportsLock.unlock() }
        if let reports, !refresh {
            return reports
        }
        let probed = transports.sorted { $0.priority < $1.priority }.map { transport -> TransportReport in
            let status = transport.probe()
            log.debug("Transport \(transport.id): \(status.isAvailable ? "available" : "unavailable") - \(status.summary)")
            return TransportReport(transport: transport, status: status)
        }
        reports = probed
        return probed
    }

    /// Finds every reachable monitor and resolves its profile.
    public func discoverMonitors(settings: AppSettings? = nil, readCapabilities: Bool = true) throws -> [ResolvedMonitor] {
        let reports = probeTransports()
        let available = reports.filter { $0.status.isAvailable }.map(\.transport)
        if available.isEmpty {
            let details = reports.map { "\($0.transport.displayName): \($0.status.summary)." }.joined(separator: " ")
            throw DdcError(.noSupportedTransport, "No supported DDC/CI interface was found. \(details)",
                           hint: reports.compactMap(\.status.hint).first)
        }

        var endpoints: [DisplayEndpoint] = []
        for transport in available {
            do {
                endpoints.append(contentsOf: try transport.enumerate())
            } catch {
                log.warn("Listing monitors through \(transport.id) failed.", error)
            }
        }

        let currentState = state.load()
        var monitors: [ResolvedMonitor] = []
        for group in SwitchService.groupByMonitor(endpoints) {
            let ordered = group.sorted { $0.transport.priority < $1.transport.priority }
            monitors.append(resolve(bestIdentity(ordered.map(\.identity)), ordered, currentState, settings, readCapabilities))
        }

        // A monitor showing another computer may vanish from the OS. If we have reached it
        // before, try its last known address so the user can still switch.
        for (stableId, monitorState) in currentState.monitors.sorted(by: { $0.key < $1.key }) {
            guard !monitors.contains(where: { $0.stableId == stableId }),
                  let record = monitorState.lastEndpoint, let identity = monitorState.identity,
                  let transport = available.first(where: { $0.id == record.transport }),
                  let restored = transport.restore(record, identity: identity) else { continue }
            log.info("\(identity) is not listed by the OS; using its last known address \(record.transport) \(record.address).")
            monitors.append(resolve(identity, [restored], currentState, settings, false))
        }

        return monitors.sorted { lhs, rhs in
            let a = lhs.osDisplayName ?? "~", b = rhs.osDisplayName ?? "~"
            let byDisplay = a.caseInsensitiveCompare(b)
            if byDisplay != .orderedSame { return byDisplay == .orderedAscending }
            return lhs.name.caseInsensitiveCompare(rhs.name) == .orderedAscending
        }
    }

    static func hasSerial(_ identity: MonitorIdentity) -> Bool {
        identity.serialNumber != 0 || !(identity.serialString?.trimmingCharacters(in: .whitespaces).isEmpty ?? true)
    }

    /// The identity with a serial (then with a model name) wins; first one on ties.
    private func bestIdentity(_ identities: [MonitorIdentity]) -> MonitorIdentity {
        func score(_ i: MonitorIdentity) -> Int { (SwitchService.hasSerial(i) ? 2 : 0) + (i.modelName != nil ? 1 : 0) }
        var best = identities[0]
        for identity in identities.dropFirst() where score(identity) > score(best) {
            best = identity
        }
        return best
    }

    /// Groups endpoints from all transports into physical monitors. Endpoints whose EDID came back
    /// without a serial join the serial-bearing monitor of the same model on the same OS display,
    /// or the only one of that model.
    static func groupByMonitor(_ endpoints: [DisplayEndpoint]) -> [[DisplayEndpoint]] {
        var result: [[DisplayEndpoint]] = []
        var modelOrder: [String] = []
        var byModel: [String: [DisplayEndpoint]] = [:]
        for endpoint in endpoints {
            let key = "\(endpoint.identity.manufacturerId)-\(endpoint.identity.productCode)"
            if byModel[key] == nil { modelOrder.append(key) }
            byModel[key, default: []].append(endpoint)
        }

        for key in modelOrder {
            let model = byModel[key] ?? []
            var withSerial: [[DisplayEndpoint]] = []
            var indexById: [String: Int] = [:]
            for endpoint in model where hasSerial(endpoint.identity) {
                if let index = indexById[endpoint.identity.stableId] {
                    withSerial[index].append(endpoint)
                } else {
                    indexById[endpoint.identity.stableId] = withSerial.count
                    withSerial.append([endpoint])
                }
            }

            var orphans: [DisplayEndpoint] = []
            for endpoint in model where !hasSerial(endpoint.identity) {
                let sameDisplay: [Int]? = endpoint.osDisplayName.map { name in
                    withSerial.indices.filter { i in
                        withSerial[i].contains { $0.osDisplayName?.caseInsensitiveCompare(name) == .orderedSame }
                    }
                }
                if let sameDisplay, sameDisplay.count == 1 {
                    withSerial[sameDisplay[0]].append(endpoint)
                } else if withSerial.count == 1 {
                    withSerial[0].append(endpoint)
                } else {
                    orphans.append(endpoint)
                }
            }

            result.append(contentsOf: withSerial)
            var orphanOrder: [String] = []
            var orphanGroups: [String: [DisplayEndpoint]] = [:]
            for endpoint in orphans {
                let groupKey = (endpoint.osDisplayName ?? "\(endpoint.transport.id):\(endpoint.connectorId)").lowercased()
                if orphanGroups[groupKey] == nil { orphanOrder.append(groupKey) }
                orphanGroups[groupKey, default: []].append(endpoint)
            }
            result.append(contentsOf: orphanOrder.compactMap { orphanGroups[$0] })
        }
        return result
    }

    private func resolve(_ identity: MonitorIdentity, _ endpoints: [DisplayEndpoint], _ currentState: AppState, _ settings: AppSettings?, _ readCapabilities: Bool) -> ResolvedMonitor {
        let forcedProfile = settings?.findMonitor(identity.stableId)?.profileId
        var capabilities = currentState.monitors[identity.stableId]?.capabilities.map(CapabilitiesParser.parse)

        var match = catalog.resolve(identity, capabilities, forcedProfileId: forcedProfile)
        if match.quality < .capabilitiesModel && capabilities == nil && readCapabilities {
            capabilities = readAndCacheCapabilities(identity: identity, endpoints: endpoints)
            if capabilities != nil {
                match = catalog.resolve(identity, capabilities, forcedProfileId: forcedProfile)
            }
        }
        return ResolvedMonitor(identity: identity, endpoints: endpoints, profile: match.profile, quality: match.quality, capabilities: capabilities)
    }

    /// Reads the capabilities string through the first endpoint that supports it, and caches it.
    public func readAndCacheCapabilities(identity: MonitorIdentity, endpoints: [DisplayEndpoint]) -> MonitorCapabilities? {
        for endpoint in endpoints {
            let raw: String?
            do {
                raw = try busLock.withLock(timeout: SwitchService.busTimeout) { endpoint.transport.readCapabilities(endpoint) }
            } catch {
                log.warn("Reading capabilities of \(identity) through \(endpoint.transport.id) failed.", error)
                continue
            }
            if let raw, !raw.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                log.info("Capabilities of \(identity): \(raw)")
                _ = try? state.update { s in
                    let m = s.getOrAdd(identity.stableId)
                    m.capabilities = raw
                    if m.identity == nil { m.identity = identity }
                }
                return CapabilitiesParser.parse(raw)
            }
        }
        return nil
    }

    /// Switches to an input listed in the monitor's profile.
    public func switchTo(_ monitor: ResolvedMonitor, inputId: String, settings: MonitorSettings? = nil) throws -> SwitchOutcome {
        guard let input = monitor.profile.findInput(inputId) else {
            throw DdcError(.inputNotInProfile,
                           "'\(inputId)' is not an input of \(monitor.name). Choose one of: \(InputResolver.describe(monitor.profile.inputs, nil)).")
        }

        if monitor.profile.effectiveQuirks.ignoresCommandsWhenInactive,
           let local = settings?.localInputId, local.caseInsensitiveCompare(input.id) == .orderedSame,
           isAnswering(monitor) == false {
            throw DdcError(.monitorOnOtherInput,
                           "\(monitor.name) is showing another input and ignores this computer until it is switched back.",
                           hint: "Switch back from the other computer (Monitor Switcher runs on Windows and macOS) or with the monitor's buttons.")
        }

        let outcome = try deliver(monitor, monitor.inputProtocol, input.codeValue, monitor.profile.effectiveQuirks, input.id, input.name)
        remember(monitor, outcome.endpoint, lastInputId: input.id)
        return outcome
    }

    /// Switches between the monitor's two toggle inputs.
    public func toggle(_ monitor: ResolvedMonitor, settings: MonitorSettings?) throws -> SwitchOutcome {
        try switchTo(monitor, inputId: try toggleTarget(monitor, settings: settings), settings: settings)
    }

    public func toggleTarget(_ monitor: ResolvedMonitor, settings: MonitorSettings?) throws -> String {
        guard let pair = ToggleLogic.toggleInputs(monitor.profile, settings) else {
            throw DdcError(.configuration, "No toggle inputs are set for \(monitor.name).",
                           hint: "Choose the two inputs to toggle between in Settings.")
        }
        let last = state.load().monitors[monitor.stableId]?.lastInputId
        return ToggleLogic.nextInput(pair.a, pair.b, lastInputId: last, localInputId: settings?.localInputId,
                                     ignoresCommandsWhenInactive: monitor.profile.effectiveQuirks.ignoresCommandsWhenInactive)
    }

    /// Does the monitor answer a DDC/CI read right now? nil when no transport can read. For monitors
    /// that ignore commands on inactive inputs, false means it is showing another input (on the
    /// LG 32UN880 reads go silent while it shows the other computer).
    public func isAnswering(_ monitor: ResolvedMonitor) -> Bool? {
        let readers = monitor.endpoints.filter { $0.transport.supportsReads }
        if readers.isEmpty { return nil }
        do {
            return try busLock.withLock(timeout: SwitchService.busTimeout) {
                readers.contains { $0.transport.readVcp($0, code: 0x10) != nil }
            }
        } catch {
            log.debug("Read failed: \(error)")
            return false
        }
    }

    /// Setup wizard only: sends a code that may not be in the profile yet. The code must be one of
    /// the protocol's own candidates, or a profile code when the profile uses this protocol.
    public func sendCandidate(_ monitor: ResolvedMonitor, using inputProtocol: any InputProtocol, candidate: InputCandidate) throws -> SwitchOutcome {
        let allowed = inputProtocol.candidates.contains { $0.code == candidate.code }
            || inputProtocol.candidates(for: monitor.capabilities).contains { $0.code == candidate.code }
            || (inputProtocol.id.caseInsensitiveCompare(monitor.profile.protocolId) == .orderedSame
                && monitor.profile.inputs.contains { $0.codeValue == candidate.code })
        guard allowed else {
            throw DdcError.safety("\(Hex.format(candidate.code)) is not a \(inputProtocol.displayName) candidate for \(candidate.name).")
        }
        let outcome = try deliver(monitor, inputProtocol, candidate.code, ProfileQuirks(), candidate.inputId, candidate.name)
        remember(monitor, outcome.endpoint, lastInputId: nil)
        return outcome
    }

    private func deliver(_ monitor: ResolvedMonitor, _ inputProtocol: any InputProtocol, _ code: UInt16, _ quirks: ProfileQuirks, _ inputId: String, _ inputName: String) throws -> SwitchOutcome {
        guard let endpoint = monitor.endpoint(for: inputProtocol) else {
            throw DdcError(.unsupportedByTransport,
                           "\(monitor.name) needs the \(inputProtocol.displayName) command, and no interface that can send it reaches this monitor.",
                           hint: DdcHints.unsupportedPort)
        }

        let steps = inputProtocol.buildSwitch(code: code)
        let start = Date()
        var attempts = 0
        try busLock.withLock(timeout: SwitchService.busTimeout) {
            while true {
                attempts += 1
                do {
                    for step in steps {
                        try endpoint.transport.send(endpoint, step.frame)
                        if step.delayAfterMs > 0 {
                            Thread.sleep(forTimeInterval: Double(step.delayAfterMs) / 1000)
                        }
                    }
                    break
                } catch let error as DdcError where (error.kind == .transportFailure || error.kind == .monitorNotResponding) && attempts <= quirks.retries {
                    log.warn("Attempt \(attempts) to switch \(monitor.name) to \(inputName) failed; retrying.", error)
                    Thread.sleep(forTimeInterval: Double(quirks.retryDelayMs) / 1000)
                }
            }
            if quirks.postSwitchDelayMs > 0 {
                Thread.sleep(forTimeInterval: Double(quirks.postSwitchDelayMs) / 1000)
            }
        }

        let elapsed = Date().timeIntervalSince(start)
        let frames = steps.map { "\($0.frame)" }.joined(separator: " ; ")
        log.info("Switched \(monitor.name) [\(monitor.stableId)] to \(inputName) (\(inputId), \(Hex.format(code))) via \(endpoint.transport.id) \(endpoint.connectorId): \(frames) in \(Int(elapsed * 1000)) ms, \(attempts) attempt(s).")
        return SwitchOutcome(monitor: monitor, inputId: inputId, inputName: inputName, code: code, endpoint: endpoint, attempts: attempts, elapsed: elapsed)
    }

    private func remember(_ monitor: ResolvedMonitor, _ endpoint: DisplayEndpoint, lastInputId: String?) {
        let record = endpoint.transport.describe(endpoint)
        do {
            try state.update { s in
                let m = s.getOrAdd(monitor.stableId)
                m.identity = monitor.identity
                if let record { m.lastEndpoint = record }
                if let lastInputId {
                    m.lastInputId = lastInputId
                    m.lastSwitchedAt = Date()
                }
            }
        } catch {
            // The switch already happened; failing to save state must not turn it into an error.
            log.warn("Could not save state after switching.", error)
        }
    }
}

/// Everything a front end (menu bar app, CLI, wizard) needs, wired up by the platform layer.
public final class AppServices {
    public let paths: AppPaths
    public let log: LogSink
    public let transports: [DdcTransport]
    public let settings: JsonFileStore<AppSettings>
    public let state: JsonFileStore<AppState>
    public let switcher: SwitchService

    public init(paths: AppPaths, log: LogSink, transports: [DdcTransport], settings: JsonFileStore<AppSettings>, state: JsonFileStore<AppState>, switcher: SwitchService) {
        self.paths = paths
        self.log = log
        self.transports = transports
        self.settings = settings
        self.state = state
        self.switcher = switcher
    }

    public var profiles: ProfileCatalog { switcher.catalog }

    /// Reloads built-in and user profiles from disk.
    public func reloadProfiles() {
        switcher.reloadProfiles(ProfileCatalog.load(userProfilesDirectory: paths.userProfilesDirectory, log: log))
    }
}
