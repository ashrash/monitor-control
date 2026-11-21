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

    private(set) var services: AppServices?
    let windows = WindowPresenter()
    private let hotkeys = HotkeyManager()
    private var busy = false
    private var screenObserver: NSObjectProtocol?
    private var refreshTask: Task<Void, Never>?

    private init() {}

    func start() {
        guard services == nil else { return }
        let services = MacPlatform.create(processTag: "app")
        self.services = services
        services.log.info("Monitor Switcher \(AppInfo.version) menu bar app starting.")
        let firstRun = !FileManager.default.fileExists(atPath: services.paths.settingsFile.path)
        settings = services.settings.load()
        state = services.state.load()

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
                && (action == .toggle || h.inputId?.caseInsensitiveCompare(inputId ?? "") == .orderedSame)
        }.flatMap { Hotkey.parse($0.keys)?.display }
    }

    func menuTitle(_ monitor: ResolvedMonitor, _ input: ProfileInput) -> String {
        let label = settings.findMonitor(monitor.stableId)?.labelFor(input.id)
        let base = label.map { "\($0)  (\(input.name))" } ?? input.name
        return shortcutText(monitor, .switchInput, input.id).map { "\(base)      \($0)" } ?? base
    }

    // MARK: Switching

    func execute(monitorId: String, action: HotkeyAction, inputId: String?) {
        guard let services else { return }
        guard !busy else {
            services.log.debug("A switch is already running; ignored.")
            return
        }
        busy = true
        let currentSettings = settings
        let cached = monitors
        Task {
            defer { busy = false }
            do {
                let outcome = try await Background.run {
                    try AppModel.perform(services, currentSettings, cached, monitorId, action, inputId)
                }
                state = services.state.load()
                if currentSettings.notifyOnSwitch {
                    let ms = currentSettings.findMonitor(outcome.monitor.stableId)
                    let body = ms?.labelFor(outcome.inputId).map { "Switched to \($0) (\(outcome.inputName))" } ?? "Switched to \(outcome.inputName)"
                    Notifier.post(ms?.label ?? outcome.monitor.name, body)
                }
            } catch let error as DdcError {
                services.log.warn("Switch failed: \(error.message)")
                Notifier.post(error.kind == .monitorOnOtherInput ? "Monitor is showing another input" : "Could not switch", error.userMessage)
            } catch {
                services.log.error("Switch failed unexpectedly.", error)
                Notifier.post("Could not switch", error.localizedDescription)
            }
        }
    }

    /// Runs on a worker thread.
    nonisolated private static func perform(_ services: AppServices, _ settings: AppSettings, _ cached: [ResolvedMonitor],
                                            _ monitorId: String, _ action: HotkeyAction, _ inputId: String?) throws -> SwitchOutcome {
        var found = cached.first { $0.stableId == monitorId }
        if found == nil {
            // Displays may have changed since the last refresh.
            found = try services.switcher.discoverMonitors(settings: settings).first { $0.stableId == monitorId }
        }
        guard let monitor = found else {
            throw DdcError(.monitorNotFound, "The monitor for this shortcut (\(monitorId)) is not connected.")
        }
        let ms = settings.findMonitor(monitorId)
        if action == .toggle {
            return try services.switcher.toggle(monitor, settings: ms)
        }
        return try services.switcher.switchTo(monitor, inputId: inputId ?? "", settings: ms)
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
        _ = hotkeys.register(settings.hotkeys)
        await refresh()
    }

    func openSettings(welcome: Bool = false) {
        let draft = SettingsDraft(model: self, welcome: welcome)
        windows.show(id: "settings", title: "Monitor Switcher Settings", view: SettingsView(model: self, draft: draft, welcome: welcome))
    }

    func openWizard(_ monitor: ResolvedMonitor?) {
        guard !windows.isOpen("wizard") else {
            windows.bringToFront("wizard")
            return
        }
        let wizard = WizardModel(model: self, monitor: monitor)
        windows.show(id: "wizard", title: "Set up a monitor", view: SetupWizardView(wizard: wizard), shouldClose: { [weak wizard] in
            wizard?.requestClose() ?? true
        })
    }

    func openLogFolder() {
        guard let services else { return }
        try? FileManager.default.createDirectory(at: services.paths.logsDirectory, withIntermediateDirectories: true)
        NSWorkspace.shared.open(services.paths.logsDirectory)
    }
}
