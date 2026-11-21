import AppKit
import MonitorSwitcherCore
import SwiftUI

@main
struct MonitorSwitcherApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    var body: some Scene {
        MenuBarExtra("Monitor Switcher", systemImage: "display") {
            MenuContent(model: AppModel.shared)
        }
        .menuBarExtraStyle(.menu)
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        Task { @MainActor in
            // Info.plist sets LSUIElement; this also hides the Dock icon under `swift run`.
            NSApp.setActivationPolicy(.accessory)
            AppModel.shared.start()
        }
    }
}

/// The menu. Mirrors the Windows tray menu: per monitor, its inputs (checkmark on the last input
/// sent, shortcut hint), Toggle, then setup, settings, logs and quit.
struct MenuContent: View {
    @ObservedObject var model: AppModel

    var body: some View {
        if let error = model.discoveryError {
            Text(error.count > 120 ? String(error.prefix(117)) + "..." : error)
            Divider()
        } else if model.monitors.isEmpty {
            Text("No DDC/CI monitors found")
            Divider()
        }

        ForEach(model.monitors, id: \.stableId) { monitor in
            Text(model.label(for: monitor))
            ForEach(monitor.profile.inputs, id: \.id) { input in
                Toggle(model.menuTitle(monitor, input), isOn: Binding(
                    get: { model.isLastSent(monitor, input) },
                    set: { _ in model.execute(monitorId: monitor.stableId, action: .switchInput, inputId: input.id) }))
            }
            if let pair = ToggleLogic.toggleInputs(monitor.profile, model.settings.findMonitor(monitor.stableId)) {
                Button(toggleTitle(monitor, pair)) {
                    model.execute(monitorId: monitor.stableId, action: .toggle, inputId: nil)
                }
            }
            if monitor.needsSetup {
                Button("Set up this monitor…") { model.openWizard(monitor) }
            }
            Divider()
        }

        Button("Set up a monitor…") { model.openWizard(nil) }
        Button("Settings…") { model.openSettings() }
        Button("Open log folder") { model.openLogFolder() }
        Divider()
        Button("Quit Monitor Switcher") { NSApp.terminate(nil) }
            .keyboardShortcut("q")
    }

    private func toggleTitle(_ monitor: ResolvedMonitor, _ pair: (a: String, b: String)) -> String {
        let ms = model.settings.findMonitor(monitor.stableId)
        func name(_ id: String) -> String { ms?.labelFor(id) ?? monitor.profile.findInput(id)?.name ?? id }
        let base = "Toggle  (\(name(pair.a)) / \(name(pair.b)))"
        return model.shortcutText(monitor, .toggle, nil).map { "\(base)      \($0)" } ?? base
    }
}
