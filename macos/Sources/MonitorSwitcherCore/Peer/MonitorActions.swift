import Foundation

/// What a switch or brightness/volume change did, locally or through the paired computer.
public struct ActionResult {
    public let monitorName: String
    public let inputId: String?
    public let inputName: String?
    public let control: MonitorControl?
    public let percent: Int?
    /// Name of the paired computer when it carried out the request.
    public let viaPeer: String?
    /// Local transport id and duration, when done locally.
    public let transportId: String?
    public let elapsed: TimeInterval?

    /// "via ioavservice in 3 ms" or "via DESKTOP-PC".
    public var viaText: String {
        if let viaPeer { return "via \(viaPeer)" }
        let ms = elapsed.map { " in \(Int($0 * 1000)) ms" } ?? ""
        return "via \(transportId ?? "?")\(ms)"
    }
}

/// The actions every front end offers (menu bar or tray app, CLI, hotkeys). Mirrors `MonitorActions.cs`.
///
/// Some monitors (the LG 32UN880) only obey the computer they are showing. When this computer is
/// paired with the other one and the monitor has stopped answering here, the request goes to the
/// other computer, which carries it out with its own profiles and safety checks.
public final class MonitorActions {
    public let services: AppServices
    public let machine: PeerMachine
    private let peer: PeerChannel?

    public init(services: AppServices, machine: PeerMachine, peer: PeerChannel?) {
        self.services = services
        self.machine = machine
        self.peer = peer
    }

    public var isPaired: Bool { peer != nil }

    private var switcher: SwitchService { services.switcher }

    // MARK: Acting

    public func switchInput(_ monitor: ResolvedMonitor, inputId: String, settings: MonitorSettings?) throws -> ActionResult {
        if shouldRelay(monitor) {
            return try relaySwitch(monitor, inputId: inputId)
        }
        let outcome = try switcher.switchTo(monitor, inputId: inputId, settings: settings)
        return ActionResult(monitorName: monitor.name, inputId: outcome.inputId, inputName: outcome.inputName, control: nil, percent: nil,
                            viaPeer: nil, transportId: outcome.endpoint.transport.id, elapsed: outcome.elapsed)
    }

    /// `pair` overrides the toggle inputs from settings (CLI `--toggle a,b`).
    public func toggle(_ monitor: ResolvedMonitor, settings: MonitorSettings?, pair: (a: String, b: String)? = nil) throws -> ActionResult {
        if shouldRelay(monitor) {
            // The monitor is showing the other computer, so "toggle" means "come back here".
            guard let local = settings?.localInputId, monitor.profile.findInput(local) != nil else {
                throw DdcError(.configuration, "\(monitor.name) is showing another input, and it is not set which input this computer uses.",
                               hint: "Choose the input this computer is plugged into in Settings.")
            }
            return try relaySwitch(monitor, inputId: local)
        }
        let target: String
        if let pair {
            let last = services.state.load().monitors[monitor.stableId]?.lastInputId
            target = ToggleLogic.nextInput(pair.a, pair.b, lastInputId: last, localInputId: settings?.localInputId,
                                           ignoresCommandsWhenInactive: monitor.profile.effectiveQuirks.ignoresCommandsWhenInactive)
        } else {
            target = try switcher.toggleTarget(monitor, settings: settings)
        }
        return try switchInput(monitor, inputId: target, settings: settings)
    }

    public func setControl(_ monitor: ResolvedMonitor, _ control: MonitorControl, _ change: ControlChange) throws -> ActionResult {
        if shouldRelay(monitor) {
            var request = newRequest(.control, monitor)
            request.control = control.rawValue
            switch change {
            case .set(let value): request.setPercent = value
            case .adjust(let delta): request.adjustPercent = delta
            }
            let reply = try relay(request)
            return ActionResult(monitorName: monitor.name, inputId: nil, inputName: nil, control: control, percent: reply.percent,
                                viaPeer: reply.from, transportId: nil, elapsed: nil)
        }
        let outcome = try switcher.setControl(monitor, control, change)
        return ActionResult(monitorName: monitor.name, inputId: nil, inputName: nil, control: control, percent: outcome.percent,
                            viaPeer: nil, transportId: outcome.endpoint.transport.id, elapsed: nil)
    }

    /// Checks that the paired computer answers and accepts our pairing code.
    public func ping() throws -> PeerReply {
        try relay(PeerRequest(machine: machine.id, from: machine.name, kind: .ping))
    }

    /// Relay only when it is needed and can help: paired, a monitor that ignores inactive
    /// computers, and it does not answer this one right now.
    public func shouldRelay(_ monitor: ResolvedMonitor) -> Bool {
        guard peer != nil, monitor.profile.effectiveQuirks.ignoresCommandsWhenInactive else { return false }
        return switcher.isAnswering(monitor) == false
    }

    private func relaySwitch(_ monitor: ResolvedMonitor, inputId: String) throws -> ActionResult {
        guard let input = monitor.profile.findInput(inputId) else {
            throw DdcError(.inputNotInProfile, "'\(inputId)' is not an input of \(monitor.name).")
        }
        var request = newRequest(.switchInput, monitor)
        request.inputId = input.id
        let reply = try relay(request)
        _ = try? services.state.update { s in
            let m = s.getOrAdd(monitor.stableId)
            m.identity = monitor.identity
            m.lastInputId = input.id
            m.lastSwitchedAt = Date()
        }
        return ActionResult(monitorName: monitor.name, inputId: input.id, inputName: reply.inputName ?? input.name, control: nil, percent: nil,
                            viaPeer: reply.from, transportId: nil, elapsed: nil)
    }

    private func newRequest(_ kind: PeerRequestKind, _ monitor: ResolvedMonitor) -> PeerRequest {
        PeerRequest(machine: machine.id, from: machine.name, kind: kind, monitor: PeerMonitorRef(monitor))
    }

    private func relay(_ request: PeerRequest) throws -> PeerReply {
        guard let peer else {
            throw DdcError(.peerUnavailable, "This computer is not paired with another one.", hint: "Enter the same pairing code on both computers in Settings.")
        }
        let reply = try peer.send(request)
        guard reply.ok else {
            throw DdcError(.peerUnavailable, "\(reply.from) could not do it: \(reply.message)")
        }
        services.log.info("\(reply.from) carried out \(request.kind.rawValue) for this computer: \(reply.message)")
        return reply
    }

    // MARK: Answering the other computer

    /// Carries out a request from the paired computer. Never relays again, so two computers can't
    /// bounce a request between them.
    public func handle(_ request: PeerRequest, settings: AppSettings) -> PeerReply {
        func fail(_ message: String) -> PeerReply {
            services.log.warn("Request from \(request.from) (\(request.kind.rawValue)) failed: \(message)")
            return PeerReply(id: request.id, from: machine.name, ok: false, message: message)
        }

        if request.kind == .ping {
            return PeerReply(id: request.id, from: machine.name, ok: true, message: "Monitor Switcher on \(machine.name) is ready.")
        }
        guard let ref = request.monitor else { return fail("The request did not say which monitor.") }

        do {
            let monitors = try switcher.discoverMonitors(settings: settings)
            guard let monitor = ref.find(in: monitors) else {
                return fail("That monitor (\(ref.manufacturerId), serial \(ref.serialNumber)) is not connected to \(machine.name).")
            }
            let ms = settings.findMonitor(monitor.stableId)
            switch request.kind {
            case .switchInput:
                guard let inputId = request.inputId else { return fail("The request did not say which input.") }
                let outcome = try switcher.switchTo(monitor, inputId: inputId, settings: ms)
                services.log.info("Switched \(monitor.name) to \(outcome.inputName) for \(request.from).")
                return PeerReply(id: request.id, from: machine.name, ok: true, message: "Switched to \(outcome.inputName).",
                                 inputId: outcome.inputId, inputName: outcome.inputName)
            case .control:
                guard let control = request.control.flatMap(MonitorControl.named), let change = request.controlChange else {
                    return fail("The request did not say what to change.")
                }
                let outcome = try switcher.setControl(monitor, control, change)
                return PeerReply(id: request.id, from: machine.name, ok: true, message: "\(control.displayName) \(outcome.percent)%.", percent: outcome.percent)
            case .ping:
                return PeerReply(id: request.id, from: machine.name, ok: true, message: "Ready.")
            }
        } catch let error as DdcError {
            return fail(error.userMessage)
        } catch {
            return fail(error.localizedDescription)
        }
    }
}
