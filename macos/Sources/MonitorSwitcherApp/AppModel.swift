import AppKit
import MonitorSwitcherCore
import MonitorSwitcherMac

/// State behind the menu bar item: monitors, settings, hotkeys and switching. Mirrors the Windows
/// `TrayContext`: switching runs off the main thread, one switch at a time, and monitors are
/// refreshed when displays change (no polling).
@MainActor
final class AppModel: ObservableObject {
    static let shared = AppModel()

    @Published private(set) var monitors: [ResolvedMonitor] = []
    @Published private(set) var discoveryError: String?
    @Published private(set) var settings = AppSettings()
    @Published private(set) var state = AppState()
    /// Last known brightness/volume per monitor, in percent.
    @Published private(set) var levels: [String: [MonitorControl: Int]] = [:]

    private(set) var services: AppServices?
    let windows = WindowPresenter()
    private let hotkeys = HotkeyManager()
    private var busy = false
    private var screenObserver: NSObjectProtocol?
    private var refreshTask: Task<Void, Never>?
    private(set) var actions: MonitorActions?
    private var listener: UdpPeerListener?
    private var listenerConfig: (port: UInt16, paired: Bool)?

    private init() {}

    func start() {
        guard services == nil else { return }
        let services = MacPlatform.create(processTag: "app")
        self.services = services
        services.log.info("Monitor Switcher \(AppInfo.version) menu bar app starting.")
        let firstRun = !FileManager.default.fileExists(atPath: services.paths.settingsFile.path)
        settings = services.settings.load()
        state = services.state.load()
        applyPeerSettings()

        hotkeys.onHotkey = { [weak self] binding in
            services.log.debug("Hotkey \(binding.keys) pressed.")
            self?.execute(monitorId: binding.monitorId, action: binding.action, inputId: binding.inputId)
        }
        let problems = hotkeys.register(settings.hotkeys)

        screenObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.scheduleRefresh() }
        }

        Task {
            await refresh()
            if firstRun {
                openSettings(welcome: true)
            } else if !problems.isEmpty {
                Notifier.post("Some shortcuts are unavailable", problems.joined(separator: " "))
            } else if monitors.contains(where: { $0.needsSetup }) {
                Notifier.post("New monitor found", "Open the menu and choose \"Set up a monitor\" to find the input codes that work.")
            }
        }
    }

    // MARK: Monitors

    func refresh() async {
        guard let services else { return }
        let currentSettings = settings
        do {
            monitors = try await Background.run { try services.switcher.discoverMonitors(settings: currentSettings) }
            discoveryError = nil
        } catch let error as DdcError {
            monitors = []
            discoveryError = error.userMessage
            services.log.warn("Monitor discovery failed: \(error.message)")
        } catch {
            monitors = []
            discoveryError = "Could not list monitors: \(error.localizedDescription)"
            services.log.error("Monitor discovery failed.", error)
        }
        state = services.state.load()
        await refreshLevels()
    }

    /// Reads brightness and volume of every monitor that answers (off the main thread).
    func refreshLevels() async {
        guard let services else { return }
        let current = monitors
        let read = (try? await Background.run { () -> [String: [MonitorControl: Int]] in
            var result: [String: [MonitorControl: Int]] = [:]
            for monitor in current {
                for control in MonitorControl.allCases where AppModel.supports(monitor, control) {
                    if let reading = services.switcher.readControl(monitor, control) {
                        result[monitor.stableId, default: [:]][control] = ControlMath.percent(reading)
                    }
                }
            }
            return result
        }) ?? [:]
        levels = read
    }

    nonisolated static func supports(_ monitor: ResolvedMonitor, _ control: MonitorControl) -> Bool {
        guard let capabilities = monitor.capabilities, !capabilities.vcp.isEmpty else { return true }
        return capabilities.supportsVcp(control.vcpCode)
    }

    func level(_ monitor: ResolvedMonitor, _ control: MonitorControl) -> Int? {
        levels[monitor.stableId]?[control] ?? state.monitors[monitor.stableId]?.levels[control.rawValue]
    }

    /// Display changes arrive in bursts; wait for them to settle.
    private func scheduleRefresh() {
        refreshTask?.cancel()
        refreshTask = Task {
            try? await Task.sleep(nanoseconds: 1_500_000_000)
            guard !Task.isCancelled else { return }
            await refresh()
        }
    }

    func monitor(id: String) -> ResolvedMonitor? { monitors.first { $0.stableId == id } }

    func label(for monitor: ResolvedMonitor) -> String { settings.findMonitor(monitor.stableId)?.label ?? monitor.name }

    func isLastSent(_ monitor: ResolvedMonitor, _ input: ProfileInput) -> Bool {
        state.monitors[monitor.stableId]?.lastInputId?.caseInsensitiveCompare(input.id) == .orderedSame
    }

    func shortcutText(_ monitor: ResolvedMonitor, _ action: HotkeyAction, _ inputId: String?) -> String? {
        settings.hotkeys.first { h in
            h.monitorId == monitor.stableId && h.action == action
                && (action != .switchInput || h.inputId?.caseInsensitiveCompare(inputId ?? "") == .orderedSame)
        }.flatMap { Hotkey.parse($0.keys)?.display }
    }

    func menuTitle(_ monitor: ResolvedMonitor, _ input: ProfileInput) -> String {
        let label = settings.findMonitor(monitor.stableId)?.labelFor(input.id)
        let base = label.map { "\($0)  (\(input.name))" } ?? input.name
        return shortcutText(monitor, .switchInput, input.id).map { "\(base)      \($0)" } ?? base
    }

    // MARK: Switching

    func execute(monitorId: String, action: HotkeyAction, inputId: String?) {
        if let (control, change) = action.controlChange {
            changeLevel(monitorId: monitorId, control, change)
            return
        }
        run(monitorId: monitorId) { actions, monitor, ms in
            action == .toggle ? try actions.toggle(monitor, settings: ms) : try actions.switchInput(monitor, inputId: inputId ?? "", settings: ms)
        }
    }

    func changeLevel(monitorId: String, _ control: MonitorControl, _ change: ControlChange) {
        run(monitorId: monitorId) { actions, monitor, _ in try actions.setControl(monitor, control, change) }
    }

    /// Runs one action off the main thread, one at a time, and reports the result.
    private func run(monitorId: String, _ body: @escaping (MonitorActions, ResolvedMonitor, MonitorSettings?) throws -> ActionResult) {
        guard let services, let actions else { return }
        guard !busy else {
            services.log.debug("An action is already running; ignored.")
            return
        }
        busy = true
        let currentSettings = settings
        let cached = monitors
        Task {
            defer { busy = false }
            do {
                let result = try await Background.run { () -> ActionResult in
                    let monitor = try AppModel.find(services, currentSettings, cached, monitorId)
                    return try body(actions, monitor, currentSettings.findMonitor(monitor.stableId))
                }
                state = services.state.load()
                if let control = result.control {
                    if let percent = result.percent { levels[monitorId, default: [:]][control] = percent }
                } else if currentSettings.notifyOnSwitch {
                    let ms = currentSettings.findMonitor(monitorId)
                    let name = result.inputName ?? result.inputId ?? "?"
                    var body = ms?.labelFor(result.inputId ?? "").map { "Switched to \($0) (\(name))" } ?? "Switched to \(name)"
                    if let peer = result.viaPeer { body += " by \(peer)" }
                    Notifier.post(ms?.label ?? result.monitorName, body)
                }
            } catch let error as DdcError {
                services.log.warn("Action failed: \(error.message)")
                let title: String
                switch error.kind {
                case .monitorOnOtherInput: title = "Monitor is showing another input"
                case .peerUnavailable: title = "The other computer did not respond"
                default: title = "Could not do that"
                }
                Notifier.post(title, error.userMessage)
            } catch {
                services.log.error("Action failed unexpectedly.", error)
                Notifier.post("Could not do that", error.localizedDescription)
            }
        }
    }

    /// Runs on a worker thread.
    nonisolated private static func find(_ services: AppServices, _ settings: AppSettings, _ cached: [ResolvedMonitor], _ monitorId: String) throws -> ResolvedMonitor {
        if let monitor = cached.first(where: { $0.stableId == monitorId }) { return monitor }
        // Displays may have changed since the last refresh.
        if let monitor = try services.switcher.discoverMonitors(settings: settings).first(where: { $0.stableId == monitorId }) { return monitor }
        throw DdcError(.monitorNotFound, "The monitor for this shortcut (\(monitorId)) is not connected.")
    }

    // MARK: Other computer

    /// Rebuilds the actions (with or without relaying) and starts, restarts or stops the listener.
    private func applyPeerSettings() {
        guard let services else { return }
        actions = MacPlatform.actions(services, settings: settings)
        let paired = settings.peer?.key != nil
        let port = settings.peer?.effectivePort ?? UInt16(PeerSettings.defaultPort)
        if let config = listenerConfig, config.port == port, config.paired == paired { return }

        listener?.stop()
        listener = nil
        listenerConfig = (port, paired)
        guard paired, let actions else { return }

        let store = services.settings
        let listener = UdpPeerListener(port: port, machine: actions.machine, log: services.log,
                                       keyProvider: { store.load().peer?.key }) { [weak self] request in
            let reply = actions.handle(request, settings: store.load())
            Task { @MainActor in self?.peerHandled(request, reply) }
            return reply
        }
        do {
            try listener.start()
            self.listener = listener
        } catch let error as DdcError {
            services.log.warn("Could not listen for the other computer: \(error.message)")
            Notifier.post("Pairing is not working", error.userMessage)
        } catch {
            services.log.error("Could not listen for the other computer.", error)
        }
    }

    private func peerHandled(_ request: PeerRequest, _ reply: PeerReply) {
        guard let services else { return }
        state = services.state.load()
        if request.kind == .control, let control = request.control.flatMap(MonitorControl.named), let percent = reply.percent,
           let monitor = request.monitor?.find(in: monitors) {
            levels[monitor.stableId, default: [:]][control] = percent
        }
        if request.kind == .switchInput, reply.ok, settings.notifyOnSwitch {
            Notifier.post("Switched by \(request.from)", reply.message)
        }
    }

    /// Asks the other computer to answer, using a pairing code that may not be saved yet.
    func testPeer(code: String) async -> String {
        guard let services else { return "Not ready." }
        guard let key = PeerKey(pairingCode: code) else {
            return "That pairing code is not valid. Use \"New code\" on one computer and type the same code on the other."
        }
        let port = settings.peer?.effectivePort ?? UInt16(PeerSettings.defaultPort)
        let machine = actions?.machine ?? PeerMachine.load(services.state, name: MacPlatform.computerName())
        do {
            let reply = try await Background.run {
                try MonitorActions(services: services, machine: machine,
                                   peer: UdpPeerChannel(key: key, port: port, state: services.state, log: services.log)).ping()
            }
            let address = services.state.load().peer?.lastAddress.map { " at \($0)" } ?? ""
            return "Connected: \(reply.from)\(address) answered."
        } catch let error as DdcError {
            return error.userMessage
        } catch {
            return error.localizedDescription
        }
    }

    // MARK: Settings and windows

    /// Saves settings, applies login item and hotkeys; returns problems to show in the window.
    func applySettings(_ updated: AppSettings, launchAtLogin: Bool) -> [String] {
        guard let services else { return [] }
        var problems: [String] = []
        do {
            try services.settings.save(updated)
        } catch {
            problems.append("Could not save settings: \(error.localizedDescription)")
        }
        settings = updated
        applyPeerSettings()
        do {
            try LoginItem.set(launchAtLogin)
        } catch {
            problems.append("Could not change the launch-at-login setting: \(error.localizedDescription)")
        }
        problems.append(contentsOf: hotkeys.register(updated.hotkeys))
        services.log.info("Settings saved (\(updated.hotkeys.count) shortcut(s)).")
        Task { await refresh() }
        return problems
    }

    func afterWizardSaved() async {
        guard let services else { return }
        settings = services.settings.load()
        applyPeerSettings()
        _ = hotkeys.register(settings.hotkeys)
        await refresh()
    }

    func openSettings(welcome: Bool = false) {
        let draft = SettingsDraft(model: self, welcome: welcome)
        windows.show(id: "settings", title: "Monitor Switcher Settings", view: SettingsView(model: self, draft: draft, welcome: welcome),
                     idealSize: CGSize(width: 640, height: 760), minSize: CGSize(width: 520, height: 360))
    }

    func openWizard(_ monitor: ResolvedMonitor?) {
        guard !windows.isOpen("wizard") else {
            windows.bringToFront("wizard")
            return
        }
        let wizard = WizardModel(model: self, monitor: monitor)
        windows.show(id: "wizard", title: "Set up a monitor", view: SetupWizardView(wizard: wizard),
                     idealSize: CGSize(width: 600, height: 560), minSize: CGSize(width: 480, height: 320),
                     shouldClose: { [weak wizard] in
            wizard?.requestClose() ?? true
        })
    }

    func openLogFolder() {
        guard let services else { return }
        try? FileManager.default.createDirectory(at: services.paths.logsDirectory, withIntermediateDirectories: true)
        NSWorkspace.shared.open(services.paths.logsDirectory)
    }
}
