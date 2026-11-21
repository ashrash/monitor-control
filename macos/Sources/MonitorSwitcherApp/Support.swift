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

    /// Shows a resizable window. It opens at `idealSize`, shrunk to fit the display it opens on
    /// (the one with the pointer, i.e. the one whose menu bar was clicked), and centred there.
    func show<V: View>(id: String, title: String, view: V,
                       idealSize: CGSize, minSize: CGSize,
                       shouldClose: (() -> Bool)? = nil) {
        if let existing = windows[id] {
            existing.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return
        }
        let controller = NSHostingController(rootView: view)
        // The user, not the SwiftUI content, decides the window size.
        controller.sizingOptions = []
        let window = NSWindow(contentViewController: controller)
        window.title = title
        window.styleMask = [.titled, .closable, .miniaturizable, .resizable]
        window.isReleasedWhenClosed = false
        window.identifier = NSUserInterfaceItemIdentifier(id)
        window.delegate = self

        let screen = Self.screenWithPointer() ?? NSScreen.main
        let visible = screen?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1280, height: 800)
        // Leave a margin so the window never touches the menu bar, Dock or screen edges.
        let maxContent = window.contentRect(forFrameRect: visible.insetBy(dx: 24, dy: 24)).size
        let content = CGSize(width: min(idealSize.width, maxContent.width),
                             height: min(idealSize.height, maxContent.height))
        window.contentMinSize = CGSize(width: min(minSize.width, content.width),
                                       height: min(minSize.height, content.height))
        window.setContentSize(content)
        let frame = window.frame
        window.setFrameOrigin(NSPoint(x: visible.midX - frame.width / 2, y: visible.midY - frame.height / 2))
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

    private static func screenWithPointer() -> NSScreen? {
        let pointer = NSEvent.mouseLocation
        return NSScreen.screens.first { NSMouseInRect(pointer, $0.frame, false) }
    }

    /// Shrinks a window that no longer fits, e.g. after it was dragged to a smaller display or the
    /// display resolution changed.
    private func fitToScreen(_ window: NSWindow) {
        guard let visible = window.screen?.visibleFrame else { return }
        var frame = window.frame
        frame.size.width = min(frame.width, visible.width)
        frame.size.height = min(frame.height, visible.height)
        frame.origin.x = min(max(frame.minX, visible.minX), visible.maxX - frame.width)
        frame.origin.y = min(max(frame.minY, visible.minY), visible.maxY - frame.height)
        if frame != window.frame {
            window.setFrame(frame, display: true, animate: false)
        }
    }

    func windowDidChangeScreen(_ notification: Notification) {
        guard let window = notification.object as? NSWindow else { return }
        fitToScreen(window)
    }

    func windowDidChangeScreenProfile(_ notification: Notification) {
        guard let window = notification.object as? NSWindow else { return }
        fitToScreen(window)
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
