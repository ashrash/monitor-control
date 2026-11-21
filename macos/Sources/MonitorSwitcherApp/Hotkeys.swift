import AppKit
import Carbon.HIToolbox
import MonitorSwitcherCore

/// A global key combination. Stored as text in the same format as Windows ("Ctrl+Option+1");
/// "Alt" and "Win" from a Windows settings file are read as Option and Command.
struct Hotkey: Equatable {
    var keyCode: UInt32
    var control = false
    var option = false
    var shift = false
    var command = false

    var carbonModifiers: UInt32 {
        var m: UInt32 = 0
        if control { m |= UInt32(controlKey) }
        if option { m |= UInt32(optionKey) }
        if shift { m |= UInt32(shiftKey) }
        if command { m |= UInt32(cmdKey) }
        return m
    }

    /// Needs Control, Option or Command (Shift alone would steal normal typing), except F13-F20.
    var isValid: Bool {
        (control || option || command) || KeyNames.isUnusedFunctionKey(keyCode)
    }

    var keyName: String { KeyNames.name(for: keyCode) ?? "Key\(keyCode)" }

    /// Text stored in settings.json.
    var storageString: String {
        var parts: [String] = []
        if control { parts.append("Ctrl") }
        if option { parts.append("Option") }
        if shift { parts.append("Shift") }
        if command { parts.append("Cmd") }
        parts.append(keyName)
        return parts.joined(separator: "+")
    }

    /// Mac-style display, e.g. ⌃⌥1.
    var display: String {
        var s = ""
        if control { s += "⌃" }
        if option { s += "⌥" }
        if shift { s += "⇧" }
        if command { s += "⌘" }
        return s + keyName
    }

    static func parse(_ text: String) -> Hotkey? {
        var hotkey = Hotkey(keyCode: UInt32.max)
        for raw in text.split(separator: "+").map({ $0.trimmingCharacters(in: .whitespaces) }) where !raw.isEmpty {
            switch raw.lowercased() {
            case "ctrl", "control": hotkey.control = true
            case "alt", "option", "opt": hotkey.option = true
            case "shift": hotkey.shift = true
            case "cmd", "command", "win", "windows", "super": hotkey.command = true
            default:
                guard hotkey.keyCode == UInt32.max, let code = KeyNames.code(for: raw) else { return nil }
                hotkey.keyCode = code
            }
        }
        return hotkey.keyCode != UInt32.max && hotkey.isValid ? hotkey : nil
    }

    static func from(event: NSEvent) -> Hotkey? {
        let code = UInt32(event.keyCode)
        guard KeyNames.name(for: code) != nil else { return nil }
        let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        return Hotkey(keyCode: code, control: flags.contains(.control), option: flags.contains(.option),
                      shift: flags.contains(.shift), command: flags.contains(.command))
    }
}

/// US-layout key names, matching the Windows names where both exist ("Quote", "LeftBracket", ...).
enum KeyNames {
    private static let table: [(String, Int)] = [
        ("A", kVK_ANSI_A), ("B", kVK_ANSI_B), ("C", kVK_ANSI_C), ("D", kVK_ANSI_D), ("E", kVK_ANSI_E),
        ("F", kVK_ANSI_F), ("G", kVK_ANSI_G), ("H", kVK_ANSI_H), ("I", kVK_ANSI_I), ("J", kVK_ANSI_J),
        ("K", kVK_ANSI_K), ("L", kVK_ANSI_L), ("M", kVK_ANSI_M), ("N", kVK_ANSI_N), ("O", kVK_ANSI_O),
        ("P", kVK_ANSI_P), ("Q", kVK_ANSI_Q), ("R", kVK_ANSI_R), ("S", kVK_ANSI_S), ("T", kVK_ANSI_T),
        ("U", kVK_ANSI_U), ("V", kVK_ANSI_V), ("W", kVK_ANSI_W), ("X", kVK_ANSI_X), ("Y", kVK_ANSI_Y),
        ("Z", kVK_ANSI_Z),
        ("0", kVK_ANSI_0), ("1", kVK_ANSI_1), ("2", kVK_ANSI_2), ("3", kVK_ANSI_3), ("4", kVK_ANSI_4),
        ("5", kVK_ANSI_5), ("6", kVK_ANSI_6), ("7", kVK_ANSI_7), ("8", kVK_ANSI_8), ("9", kVK_ANSI_9),
        ("F1", kVK_F1), ("F2", kVK_F2), ("F3", kVK_F3), ("F4", kVK_F4), ("F5", kVK_F5), ("F6", kVK_F6),
        ("F7", kVK_F7), ("F8", kVK_F8), ("F9", kVK_F9), ("F10", kVK_F10), ("F11", kVK_F11), ("F12", kVK_F12),
        ("F13", kVK_F13), ("F14", kVK_F14), ("F15", kVK_F15), ("F16", kVK_F16), ("F17", kVK_F17),
        ("F18", kVK_F18), ("F19", kVK_F19), ("F20", kVK_F20),
        ("Space", kVK_Space), ("Return", kVK_Return), ("Tab", kVK_Tab),
        ("Home", kVK_Home), ("End", kVK_End), ("PageUp", kVK_PageUp), ("PageDown", kVK_PageDown),
        ("Left", kVK_LeftArrow), ("Right", kVK_RightArrow), ("Up", kVK_UpArrow), ("Down", kVK_DownArrow),
        ("Comma", kVK_ANSI_Comma), ("Period", kVK_ANSI_Period), ("Minus", kVK_ANSI_Minus), ("Plus", kVK_ANSI_Equal),
        ("Quote", kVK_ANSI_Quote), ("Semicolon", kVK_ANSI_Semicolon), ("LeftBracket", kVK_ANSI_LeftBracket),
        ("RightBracket", kVK_ANSI_RightBracket), ("Backslash", kVK_ANSI_Backslash), ("Slash", kVK_ANSI_Slash),
        ("Backtick", kVK_ANSI_Grave),
    ]

    static func name(for code: UInt32) -> String? {
        table.first { UInt32($0.1) == code }?.0
    }

    static func code(for name: String) -> UInt32? {
        table.first { $0.0.caseInsensitiveCompare(name) == .orderedSame }.map { UInt32($0.1) }
    }

    static func isUnusedFunctionKey(_ code: UInt32) -> Bool {
        [kVK_F13, kVK_F14, kVK_F15, kVK_F16, kVK_F17, kVK_F18, kVK_F19, kVK_F20].contains { UInt32($0) == code }
    }
}

/// Global hotkeys through Carbon's RegisterEventHotKey, which needs no Accessibility permission.
@MainActor
final class HotkeyManager {
    var onHotkey: ((HotkeyBinding) -> Void)?
    private var refs: [EventHotKeyRef] = []
    private var bindings: [UInt32: HotkeyBinding] = [:]
    private var handler: EventHandlerRef?
    private static let signature: OSType = 0x4D53_5754 // "MSWT"

    /// Registers every binding; returns a sentence per shortcut that could not be registered.
    func register(_ list: [HotkeyBinding]) -> [String] {
        installHandlerIfNeeded()
        unregisterAll()
        var problems: [String] = []
        var nextId: UInt32 = 1
        for binding in list {
            guard let hotkey = Hotkey.parse(binding.keys) else {
                problems.append("\"\(binding.keys)\" is not a valid shortcut on macOS.")
                continue
            }
            var ref: EventHotKeyRef?
            let status = RegisterEventHotKey(hotkey.keyCode, hotkey.carbonModifiers,
                                             EventHotKeyID(signature: Self.signature, id: nextId),
                                             GetEventDispatcherTarget(), 0, &ref)
            if status == noErr, let ref {
                refs.append(ref)
                bindings[nextId] = binding
                nextId += 1
            } else {
                problems.append("\(hotkey.display) is already used by another app.")
            }
        }
        return problems
    }

    func unregisterAll() {
        for ref in refs {
            UnregisterEventHotKey(ref)
        }
        refs.removeAll()
        bindings.removeAll()
    }

    func fire(_ id: UInt32) {
        if let binding = bindings[id] {
            onHotkey?(binding)
        }
    }

    private func installHandlerIfNeeded() {
        guard handler == nil else { return }
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        let context = Unmanaged.passUnretained(self).toOpaque()
        InstallEventHandler(GetEventDispatcherTarget(), { _, event, userData -> OSStatus in
            guard let event, let userData else { return OSStatus(eventNotHandledErr) }
            var hotKeyId = EventHotKeyID()
            let status = GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                                           nil, MemoryLayout<EventHotKeyID>.size, nil, &hotKeyId)
            guard status == noErr else { return status }
            let manager = Unmanaged<HotkeyManager>.fromOpaque(userData).takeUnretainedValue()
            let id = hotKeyId.id
            Task { @MainActor in manager.fire(id) }
            return OSStatus(noErr)
        }, 1, &spec, context, &handler)
    }
}
