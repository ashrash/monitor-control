import AppKit
import Carbon.HIToolbox
import SwiftUI

/// Click it and press e.g. ⌃⌥2. Delete clears it, Escape cancels.
struct KeyRecorder: NSViewRepresentable {
    @Binding var hotkey: Hotkey?

    func makeNSView(context: Context) -> KeyRecorderField {
        KeyRecorderField()
    }

    func updateNSView(_ view: KeyRecorderField, context: Context) {
        view.hotkey = hotkey
        view.onChange = { newValue in hotkey = newValue }
    }
}

final class KeyRecorderField: NSView {
    var hotkey: Hotkey? {
        didSet { needsDisplay = true }
    }
    var onChange: ((Hotkey?) -> Void)?
    private var recording = false {
        didSet { needsDisplay = true }
    }
    private var hint: String?

    override var acceptsFirstResponder: Bool { true }
    override var intrinsicContentSize: NSSize { NSSize(width: 150, height: 22) }

    override func mouseDown(with event: NSEvent) {
        window?.makeFirstResponder(self)
    }

    override func becomeFirstResponder() -> Bool {
        recording = true
        return true
    }

    override func resignFirstResponder() -> Bool {
        recording = false
        hint = nil
        return true
    }

    override func keyDown(with event: NSEvent) {
        guard recording else {
            super.keyDown(with: event)
            return
        }
        let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        let code = Int(event.keyCode)
        if code == kVK_Escape && flags.isEmpty {
            window?.makeFirstResponder(nil)
            return
        }
        if (code == kVK_Delete || code == kVK_ForwardDelete) && flags.isEmpty {
            set(nil)
            window?.makeFirstResponder(nil)
            return
        }
        if code == kVK_Tab && (flags.isEmpty || flags == .shift) {
            if flags.isEmpty {
                window?.selectNextKeyView(self)
            } else {
                window?.selectPreviousKeyView(self)
            }
            return
        }
        guard let candidate = Hotkey.from(event: event) else { return }
        guard candidate.isValid else {
            hint = "\(candidate.display) - add ⌃, ⌥ or ⌘"
            needsDisplay = true
            return
        }
        set(candidate)
        window?.makeFirstResponder(nil)
    }

    /// Command-key combinations arrive here before keyDown.
    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        if recording && event.type == .keyDown {
            keyDown(with: event)
            return true
        }
        return super.performKeyEquivalent(with: event)
    }

    override func flagsChanged(with event: NSEvent) {
        guard recording else { return }
        let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        var s = ""
        if flags.contains(.control) { s += "⌃" }
        if flags.contains(.option) { s += "⌥" }
        if flags.contains(.shift) { s += "⇧" }
        if flags.contains(.command) { s += "⌘" }
        hint = s.isEmpty ? nil : s + "…"
        needsDisplay = true
    }

    private func set(_ value: Hotkey?) {
        hotkey = value
        onChange?(value)
    }

    override func draw(_ dirtyRect: NSRect) {
        let rect = bounds.insetBy(dx: 1, dy: 1)
        let path = NSBezierPath(roundedRect: rect, xRadius: 5, yRadius: 5)
        NSColor.controlBackgroundColor.setFill()
        path.fill()
        (recording ? NSColor.keyboardFocusIndicatorColor : NSColor.separatorColor).setStroke()
        path.lineWidth = recording ? 2 : 1
        path.stroke()

        let text: String
        let color: NSColor
        if recording {
            text = hint ?? "Type a shortcut…"
            color = .secondaryLabelColor
        } else if let hotkey {
            text = hotkey.display
            color = .labelColor
        } else {
            text = "Click to set"
            color = .placeholderTextColor
        }
        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: NSFont.systemFontSize),
            .foregroundColor: color,
        ]
        let size = (text as NSString).size(withAttributes: attributes)
        (text as NSString).draw(at: NSPoint(x: 8, y: (bounds.height - size.height) / 2), withAttributes: attributes)
    }
}
