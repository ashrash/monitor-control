import AppKit
import ServiceManagement
import SwiftUI
import UserNotifications

enum AppInfo {
    static var version: String {
        (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String) ?? "0.1.0"
    }

    /// True when running from an .app bundle (not `swift run`). Notifications and login items need one.
    static var isBundled: Bool {
        Bundle.main.bundleIdentifier != nil && Bundle.main.bundleURL.pathExtension == "app"
    }
}

/// Runs blocking DDC/CI and file work off the main thread.
enum Background {
    static func run<T>(_ work: @escaping () throws -> T) async throws -> T {
        try await withCheckedThrowingContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async {
                do {
                    continuation.resume(returning: try work())
                } catch {
                    continuation.resume(throwing: error)
                }
            }
        }
    }
}

/// Opens SwiftUI views in plain NSWindows on demand and drops them on close, so the idle menu bar
/// app keeps no window state in memory.
@MainActor
final class WindowPresenter: NSObject, NSWindowDelegate {
    private var windows: [String: NSWindow] = [:]
    private var shouldClose: [String: () -> Bool] = [:]

    func show<V: View>(id: String, title: String, view: V, shouldClose: (() -> Bool)? = nil) {
        if let existing = windows[id] {
            existing.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return
        }
        let controller = NSHostingController(rootView: view)
        controller.sizingOptions = .preferredContentSize
        let window = NSWindow(contentViewController: controller)
        window.title = title
        window.styleMask = [.titled, .closable, .miniaturizable]
        window.isReleasedWhenClosed = false
        window.identifier = NSUserInterfaceItemIdentifier(id)
        window.delegate = self
        window.center()
        windows[id] = window
        self.shouldClose[id] = shouldClose
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func close(id: String) {
        shouldClose[id] = nil
        windows[id]?.close()
    }

    func isOpen(_ id: String) -> Bool { windows[id] != nil }

    func bringToFront(_ id: String) {
        windows[id]?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        guard let id = sender.identifier?.rawValue, let check = shouldClose[id] else { return true }
        return check()
    }

    func windowWillClose(_ notification: Notification) {
        guard let window = notification.object as? NSWindow, let id = window.identifier?.rawValue else { return }
        windows[id] = nil
        shouldClose[id] = nil
    }
}

enum Notifier {
    private static var authorizationRequested = false

    /// Posts a user notification (only possible from an .app bundle).
    static func post(_ title: String, _ body: String) {
        guard AppInfo.isBundled else {
            NSLog("%@: %@", title, body)
            return
        }
        let center = UNUserNotificationCenter.current()
        let send = {
            let content = UNMutableNotificationContent()
            content.title = title
            content.body = body
            center.add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
        }
        if authorizationRequested {
            send()
        } else {
            authorizationRequested = true
            center.requestAuthorization(options: [.alert]) { granted, _ in
                if granted { send() }
            }
        }
    }
}

enum LoginItem {
    static var isEnabled: Bool {
        AppInfo.isBundled && SMAppService.mainApp.status == .enabled
    }

    static func set(_ enabled: Bool) throws {
        guard AppInfo.isBundled else { return }
        if enabled {
            if SMAppService.mainApp.status != .enabled {
                try SMAppService.mainApp.register()
            }
        } else if SMAppService.mainApp.status == .enabled {
            try SMAppService.mainApp.unregister()
        }
    }
}
