import MonitorSwitcherCore
import SwiftUI

/// Editable copy of the settings for the Settings window. Mirrors the Windows `SettingsForm` logic.
@MainActor
final class SettingsDraft: ObservableObject {
    struct Row: Identifiable {
        let id: String
        let portName: String
        let verified: Bool
        var label: String
        var hotkey: Hotkey?
    }

    let model: AppModel
    let working: AppSettings
    private var currentMonitorId: String?
    private var loading = false
    private var toggleTouched = false

    @Published var selectedMonitorId: String = "" {
        didSet {
            if oldValue != selectedMonitorId, let monitor = model.monitor(id: selectedMonitorId) {
                load(monitor)
            }
        }
    }
    @Published var rows: [Row] = []
    @Published var localInputId: String? {
        didSet {
            // Picking "this Mac" also makes it one end of the toggle, unless the user set the toggle by hand.
            if !loading, !toggleTouched, let id = localInputId, let monitor = currentMonitor {
                setToggleDefaults(monitor, local: id)
            }
        }
    }
    @Published var toggleA: String = "" {
        didSet { if !loading { toggleTouched = true } }
    }
    @Published var toggleB: String = "" {
        didSet { if !loading { toggleTouched = true } }
    }
    @Published var toggleHotkey: Hotkey?
    @Published var launchAtLogin: Bool
    @Published var notify: Bool
    @Published var status: String?

    init(model: AppModel, welcome: Bool) {
        self.model = model
        working = model.settings.copy()
        launchAtLogin = LoginItem.isEnabled || (welcome && AppInfo.isBundled)
        notify = working.notifyOnSwitch
        if let first = model.monitors.first {
            loading = true
            selectedMonitorId = first.stableId
            loading = false
            load(first)
        }
    }

    var currentMonitor: ResolvedMonitor? { currentMonitorId.flatMap { model.monitor(id: $0) } }

    var monitorDetails: String {
        guard let monitor = currentMonitor else { return "" }
        let origin: String
        switch monitor.profile.origin {
        case .builtIn: origin = "built-in profile"
        case .user: origin = "your profile"
        case .fallback: origin = "no profile yet"
        }
        let via = monitor.endpoint(for: monitor.inputProtocol)?.transport.displayName ?? "no compatible interface"
        return "\(monitor.profile.id) (\(origin)) - \(monitor.inputProtocol.displayName) - via \(via)"
    }

    var monitorNote: String? {
        guard let monitor = currentMonitor else { return nil }
        if monitor.needsSetup {
            return "No profile matched this monitor, so these are standard guesses. Run the setup wizard to find the codes that work."
        }
        if monitor.profile.effectiveQuirks.ignoresCommandsWhenInactive {
            return "This monitor only obeys the computer it is showing. Shortcuts here can switch it away from this Mac; to come back, use Monitor Switcher on the other computer or the monitor's buttons."
        }
        return nil
    }

    private func load(_ monitor: ResolvedMonitor) {
        if currentMonitorId != nil {
            store()
        }
        loading = true
        defer { loading = false }
        toggleTouched = false
        currentMonitorId = monitor.stableId
        let ms = working.findMonitor(monitor.stableId)

        rows = monitor.profile.inputs.map { input in
            let binding = working.hotkeys.first {
                $0.monitorId == monitor.stableId && $0.action == .switchInput && $0.inputId?.caseInsensitiveCompare(input.id) == .orderedSame
            }
            return Row(id: input.id, portName: input.name, verified: input.verified,
                       label: ms?.labelFor(input.id) ?? "", hotkey: binding.flatMap { Hotkey.parse($0.keys) })
        }
        localInputId = ms?.localInputId
        if let pair = ToggleLogic.toggleInputs(monitor.profile, ms) {
            toggleA = pair.a
            toggleB = pair.b
        } else {
            setToggleDefaults(monitor, local: ms?.localInputId)
        }
        let toggleBinding = working.hotkeys.first { $0.monitorId == monitor.stableId && $0.action == .toggle }
        toggleHotkey = toggleBinding.flatMap { Hotkey.parse($0.keys) }
    }

    /// Toggle between this Mac's input and the first verified other input.
    private func setToggleDefaults(_ monitor: ResolvedMonitor, local: String?) {
        let inputs = monitor.profile.inputs
        guard !inputs.isEmpty else { return }
        var b = inputs.firstIndex { $0.verified && $0.id != local } ?? inputs.firstIndex { $0.id != local } ?? 0
        var a = local.flatMap { l in inputs.firstIndex { $0.id == l } } ?? -1
        if a < 0 || a == b {
            a = inputs.firstIndex { $0.id != inputs[b].id } ?? 0
        }
        if a == b && inputs.count > 1 { b = (a + 1) % inputs.count }
        let wasLoading = loading
        loading = true
        toggleA = inputs[a].id
        toggleB = inputs[b].id
        loading = wasLoading
    }

    /// Copies the visible monitor's fields into the working settings.
    private func store() {
        guard let monitorId = currentMonitorId else { return }
        let ms = working.getOrAddMonitor(monitorId)
        var labels: [String: String] = [:]
        for row in rows where !row.label.trimmingCharacters(in: .whitespaces).isEmpty {
            labels[row.id] = row.label.trimmingCharacters(in: .whitespaces)
        }
        ms.inputLabels = labels
        ms.localInputId = localInputId
        ms.toggleInputs = toggleA.isEmpty || toggleB.isEmpty ? nil : [toggleA, toggleB]

        working.hotkeys.removeAll { $0.monitorId == monitorId }
        for row in rows {
            if let hotkey = row.hotkey {
                working.hotkeys.append(HotkeyBinding(monitorId: monitorId, action: .switchInput, inputId: row.id, keys: hotkey.storageString))
            }
        }
        if let toggleHotkey {
            working.hotkeys.append(HotkeyBinding(monitorId: monitorId, action: .toggle, keys: toggleHotkey.storageString))
        }
    }

    /// Returns true when the window can close.
    func save() -> Bool {
        store()
        working.notifyOnSwitch = notify

        var seen = Set<String>()
        for binding in working.hotkeys where !seen.insert(binding.keys.lowercased()).inserted {
            status = "\(Hotkey.parse(binding.keys)?.display ?? binding.keys) is used for more than one action."
            return false
        }
        for ms in working.monitors {
            if let pair = ms.toggleInputs, pair.count == 2, pair[0] == pair[1] {
                status = "Choose two different inputs to toggle between."
                return false
            }
        }

        let problems = model.applySettings(working, launchAtLogin: launchAtLogin)
        if !problems.isEmpty {
            status = problems.joined(separator: " ") + " Your other settings were saved."
            return false
        }
        return true
    }
}

struct SettingsView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var draft: SettingsDraft
    let welcome: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Form {
                if welcome {
                    Section {
                        Text("Welcome! Monitor Switcher lives in the menu bar. Name your inputs, tell it which input this Mac is plugged into, and pick shortcuts. You can come back here any time from the menu bar icon.")
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }

                if model.monitors.isEmpty {
                    Section("No monitors found") {
                        Text("No monitor that supports DDC/CI was found. \(DdcHints.ddcCiDisabled) \(DdcHints.unsupportedPort)")
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                } else {
                    monitorSection
                    inputsSection
                    toggleSection
                }

                Section("General") {
                    Toggle("Launch Monitor Switcher at login", isOn: $draft.launchAtLogin)
                        .disabled(!AppInfo.isBundled)
                    Toggle("Show a notification after each switch", isOn: $draft.notify)
                }

                if let status = draft.status {
                    Section {
                        Text(status)
                            .foregroundStyle(.red)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
            }
            .formStyle(.grouped)

            HStack {
                Button("Run setup wizard…") {
                    let monitor = draft.currentMonitor
                    // The wizard saves settings itself; close so this window can't overwrite them later.
                    model.windows.close(id: "settings")
                    model.openWizard(monitor)
                }
                Spacer()
                Button("Cancel") { model.windows.close(id: "settings") }
                    .keyboardShortcut(.cancelAction)
                Button("Save") {
                    if draft.save() {
                        model.windows.close(id: "settings")
                    }
                }
                .keyboardShortcut(.defaultAction)
            }
            .padding(16)
        }
        .frame(width: 600)
        .frame(minHeight: 420)
    }

    private var monitorSection: some View {
        Section("Monitor") {
            if model.monitors.count > 1 {
                Picker("Monitor", selection: $draft.selectedMonitorId) {
                    ForEach(model.monitors, id: \.stableId) { m in
                        Text(model.label(for: m)).tag(m.stableId)
                    }
                }
            } else if let monitor = draft.currentMonitor {
                Text(monitor.name).font(.headline)
            }
            Text(draft.monitorDetails)
                .font(.callout)
                .foregroundStyle(.secondary)
            if let note = draft.monitorNote {
                Text(note)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private var inputsSection: some View {
        Section("Inputs") {
            ForEach($draft.rows) { $row in
                HStack(spacing: 12) {
                    Text(row.verified ? "\(row.portName) ✓" : row.portName)
                        .frame(width: 110, alignment: .leading)
                        .help(row.verified ? "Confirmed to work on this model." : "Not yet confirmed on this model.")
                    TextField("Name", text: $row.label, prompt: Text("e.g. Mac or PC"))
                        .labelsHidden()
                    KeyRecorder(hotkey: $row.hotkey)
                        .frame(width: 150, height: 22)
                }
            }
            Picker("This Mac is plugged into", selection: $draft.localInputId) {
                Text("Not set").tag(String?.none)
                ForEach(draft.rows) { row in
                    Text(row.portName).tag(String?.some(row.id))
                }
            }
        }
    }

    private var toggleSection: some View {
        Section("Toggle") {
            Picker("Switch between", selection: $draft.toggleA) {
                ForEach(draft.rows) { row in Text(row.portName).tag(row.id) }
            }
            Picker("and", selection: $draft.toggleB) {
                ForEach(draft.rows) { row in Text(row.portName).tag(row.id) }
            }
            LabeledContent("Shortcut") {
                KeyRecorder(hotkey: $draft.toggleHotkey)
                    .frame(width: 150, height: 22)
            }
        }
    }
}
