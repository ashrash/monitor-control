import AppKit
import MonitorSwitcherCore
import SwiftUI

/// Finds the input codes that work on a monitor. Same flow as the Windows `SetupWizardForm`: each
/// test switches to the chosen input, counts down, always sends the switch back, then asks what
/// happened. Monitors like the LG 32UN880 ignore the switch back because they only obey the
/// computer they are showing; the wizard notices the monitor going quiet on DDC/CI, waits for the
/// user to return with the monitor's buttons, and records that quirk.
@MainActor
final class WizardModel: ObservableObject {
    enum Page {
        case noMonitors, chooseMonitor, inputs, testing, question, done, failure
        case error(String)
    }

    enum Answer { case switched, blinked, nothing }
    enum HowBack { case byItself, buttons }

    static let countdownSeconds = 8
    /// Answering again within this time after the switch-back means this computer brought it back.
    /// Returning with the monitor's buttons takes longer (live test on Windows: 4.8-4.9 s).
    static let quickReturn: TimeInterval = 2.5

    let model: AppModel
    @Published var page: Page = .noMonitors
    @Published var chosenMonitorId: String = ""
    @Published var localInputId: String?
    @Published var targetInputId: String?
    @Published var targetLabel = ""
    @Published var inputsStatus: String?
    @Published var testStatus = ""
    @Published var testDetail = ""
    @Published var testHint = ""
    @Published var answer: Answer?
    @Published var howBack: HowBack?
    @Published var questionStatus: String?
    @Published var hotkey: Hotkey?
    @Published var saveError: String?

    private(set) var monitor: ResolvedMonitor?
    private(set) var attempts: [SetupAttempt] = []
    private var attemptIndex = 0
    private(set) var testing = false
    private var endEarly = false
    private var stopRequested = false
    private var closeAfterTest = false
    private(set) var wentSilent = false
    private(set) var cameBackQuickly: Bool?
    private var result: SetupAttempt?
    private var ignoresCommandsWhenInactive: Bool?
    private var returnVerified = false
    private var keyMonitor: Any?

    init(model: AppModel, monitor: ResolvedMonitor?) {
        self.model = model
        if let monitor {
            showInputs(monitor)
        } else if model.monitors.count == 1 {
            showInputs(model.monitors[0])
        } else if model.monitors.isEmpty {
            page = .noMonitors
        } else {
            chosenMonitorId = model.monitors[0].stableId
            page = .chooseMonitor
        }
    }

    private var services: AppServices? { model.services }

    var title: String {
        switch page {
        case .noMonitors: return "No monitors found"
        case .chooseMonitor: return "Set up a monitor"
        case .inputs: return "Which inputs?"
        case .testing: return "Testing \(portName(targetInputId))"
        case .question: return "Did it switch?"
        case .done: return "All set"
        case .failure: return "No code switched to \(portName(targetInputId))"
        case .error: return "Something went wrong"
        }
    }

    var ports: [PortChoice] { monitor.map(SetupPlanner.portChoices) ?? [] }

    func portName(_ id: String?) -> String {
        ports.first { $0.inputId == id }?.name ?? id ?? ""
    }

    var introText: String {
        guard let monitor else { return "" }
        if monitor.needsSetup {
            let names = SetupPlanner.usableProtocols(monitor).map { $0.displayName }.joined(separator: " and then ")
            return "\(monitor.name) has no profile yet, so the wizard will try \(names)."
        }
        return "\(monitor.name) is a known model (\(monitor.profile.id)). The wizard will confirm its codes on your setup."
    }

    // MARK: Pages

    func chooseMonitor() {
        if let chosen = model.monitor(id: chosenMonitorId) {
            showInputs(chosen)
        }
    }

    func showInputs(_ monitor: ResolvedMonitor) {
        self.monitor = monitor
        let ms = model.settings.findMonitor(monitor.stableId)
        let choices = SetupPlanner.portChoices(monitor)
        if localInputId == nil {
            localInputId = choices.first { $0.inputId == ms?.localInputId }?.inputId
        }
        if targetInputId == nil {
            targetInputId = choices.first { $0.inputId != ms?.localInputId && monitor.profile.findInput($0.inputId)?.verified == true }?.inputId
        }
        if targetLabel.isEmpty, let target = targetInputId {
            targetLabel = ms?.labelFor(target) ?? ""
        }
        inputsStatus = nil
        page = .inputs
    }

    func startTest() {
        guard let monitor else { return }
        guard let local = localInputId, let target = targetInputId else {
            inputsStatus = "Choose both inputs."
            return
        }
        guard local != target else {
            inputsStatus = "Choose an input other than the one this Mac is plugged into."
            return
        }
        attempts = SetupPlanner.plan(monitor, localInputId: local, targetInputId: target)
        attemptIndex = 0
        guard !attempts.isEmpty else {
            inputsStatus = "There is nothing to try for \(portName(target)) with the interfaces that reach this monitor."
            return
        }
        Task { await runAttempt() }
    }

    func requestStop() { stopRequested = true }

    func endCountdownEarly() { endEarly = true }

    /// Window close button: never leave the monitor on a test input. Stop, switch back, then close.
    func requestClose() -> Bool {
        if testing {
            closeAfterTest = true
            stopRequested = true
            return false
        }
        return true
    }

    private func runAttempt() async {
        guard let monitor, let services else { return }
        let attempt = attempts[attemptIndex]
        let target = portName(targetInputId)
        testDetail = "Trying code \(Hex.format(attempt.target.code)) with \(attempt.inputProtocol.displayName) (\(attemptIndex + 1) of \(attempts.count))."
        testHint = ""
        page = .testing
        testing = true
        endEarly = false
        stopRequested = false
        wentSilent = false
        cameBackQuickly = nil
        startKeyMonitor()

        var sent = false
        var failure: DdcError?
        do {
            let answeredBefore = try await Background.run { services.switcher.isAnswering(monitor) }
            testStatus = "Switching to \(target)..."
            _ = try await Background.run { try services.switcher.sendCandidate(monitor, using: attempt.inputProtocol, candidate: attempt.target) }
            sent = true

            testHint = "Watch the monitor. Press any key on this Mac, or click \"It switched\", to come back early."
            var remaining = WizardModel.countdownSeconds
            while remaining > 0 && !endEarly && !stopRequested {
                testStatus = "Switching back in \(remaining)"
                let tick = Date()
                if answeredBefore == true && !wentSilent {
                    wentSilent = try await Background.run { services.switcher.isAnswering(monitor) } == false
                }
                // Sleep out the rest of the second in short slices so Stop and keys feel instant.
                while Date().timeIntervalSince(tick) < 1 && !endEarly && !stopRequested {
                    try? await Task.sleep(nanoseconds: 100_000_000)
                }
                remaining -= 1
            }
        } catch let error as DdcError {
            failure = error
        } catch {
            failure = DdcError(.transportFailure, error.localizedDescription)
        }

        if sent {
            await switchBack(attempt, monitor: monitor, services: services)
        }
        stopKeyMonitor()
        testing = false

        if closeAfterTest {
            model.windows.close(id: "wizard")
            return
        }
        if let failure {
            page = .error(failure.userMessage)
            return
        }
        if stopRequested {
            page = .inputs
            return
        }
        answer = wentSilent ? .switched : nil
        howBack = nil
        questionStatus = nil
        page = .question
    }

    private func switchBack(_ attempt: SetupAttempt, monitor: ResolvedMonitor, services: AppServices) async {
        testStatus = "Switching back to \(portName(localInputId))..."
        let revertedAt = Date()
        if let back = attempt.back {
            do {
                _ = try await Background.run { try services.switcher.sendCandidate(monitor, using: attempt.inputProtocol, candidate: back) }
            } catch {
                services.log.warn("Wizard: switching back failed: \(error)")
            }
        }

        guard wentSilent else { return }

        // The monitor went quiet, so it really switched. Wait until it answers this Mac again.
        testStatus = "Waiting for the screen to come back..."
        testHint = "If the monitor still shows the other input, use its buttons to switch back to \(portName(localInputId)). The wizard continues when it sees this Mac again."
        while !closeAfterTest {
            let answering = (try? await Background.run { services.switcher.isAnswering(monitor) }) ?? nil
            if answering == true {
                let waited = Date().timeIntervalSince(revertedAt)
                cameBackQuickly = waited < WizardModel.quickReturn
                services.log.info("Wizard: monitor answered again after \(String(format: "%.1f", waited)) s.")
                return
            }
            try? await Task.sleep(nanoseconds: 700_000_000)
        }
    }

    private func startKeyMonitor() {
        stopKeyMonitor()
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            Task { @MainActor in self?.endEarly = true }
            return nil
        }
    }

    private func stopKeyMonitor() {
        if let keyMonitor {
            NSEvent.removeMonitor(keyMonitor)
        }
        keyMonitor = nil
    }

    /// Whether to ask "how did the screen come back?".
    var asksHowItCameBack: Bool { cameBackQuickly != true && answer == .switched }

    func submitAnswer() {
        switch answer {
        case .switched?:
            if asksHowItCameBack && howBack == nil {
                questionStatus = "Choose how the screen came back."
                return
            }
            accept()
            page = .done
        case .blinked?, .nothing?:
            attemptIndex += 1
            if attemptIndex < attempts.count {
                Task { await runAttempt() }
            } else {
                page = .failure
            }
        case nil:
            questionStatus = "Choose what happened."
        }
    }

    /// Turns the evidence and answers into the result. Same decision table as Windows.
    private func accept() {
        result = attempts[attemptIndex]
        if cameBackQuickly == true {
            ignoresCommandsWhenInactive = false // this Mac's switch-back worked
            returnVerified = true
        } else if howBack == .buttons {
            ignoresCommandsWhenInactive = true
            returnVerified = false
        } else if howBack == .byItself && cameBackQuickly == nil {
            ignoresCommandsWhenInactive = false // no read evidence either way; trust the answer
            returnVerified = true
        } else {
            ignoresCommandsWhenInactive = nil // came back late by itself: probably the monitor's no-signal fallback
            returnVerified = false
        }
        if let result {
            services?.log.info("Wizard: \(portName(targetInputId)) works with \(result.inputProtocol.id) \(Hex.format(result.target.code)); ignoresCommandsWhenInactive=\(ignoresCommandsWhenInactive.map { "\($0)" } ?? "unknown").")
        }
        let existing = model.settings.hotkeys.first {
            $0.monitorId == monitor?.stableId && $0.action == .switchInput && $0.inputId == targetInputId
        }
        hotkey = existing.flatMap { Hotkey.parse($0.keys) }
    }

    // MARK: Done page

    var resultSummary: String {
        guard let result else { return "" }
        return "Code \(Hex.format(result.target.code)) with \(result.inputProtocol.displayName)."
    }

    var showsQuirkNote: Bool {
        ignoresCommandsWhenInactive == true || (ignoresCommandsWhenInactive == nil && monitor?.profile.effectiveQuirks.ignoresCommandsWhenInactive == true)
    }

    private func confirmed() -> [ConfirmedInput] {
        guard let result else { return [] }
        var list = [ConfirmedInput(result.target.inputId, portName(targetInputId), result.target.code, verified: true)]
        if let back = result.back {
            list.append(ConfirmedInput(back.inputId, portName(localInputId), back.code, verified: returnVerified))
        }
        return list
    }

    var needsUserProfile: Bool {
        guard let monitor, let result else { return false }
        return SetupPlanner.needsUserProfile(monitor, result.inputProtocol, confirmed(), ignoresCommandsWhenInactive: ignoresCommandsWhenInactive)
    }

    func buildProfile() -> MonitorProfile? {
        guard let monitor, let result, let services else { return nil }
        let existingUser = services.profiles.profiles.first {
            $0.origin == .user && ProfileMatcher.evaluate($0, monitor.identity, monitor.capabilities) == .productCode
        }
        return SetupPlanner.buildUserProfile(monitor, result.inputProtocol, confirmed(),
                                             ignoresCommandsWhenInactive: ignoresCommandsWhenInactive, existingUserProfile: existingUser)
    }

    func copyProfileToPasteboard() {
        guard let profile = buildProfile() else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(ProfileCatalog.serialize(profile), forType: .string)
    }

    func save() {
        guard let monitor, let services, let local = localInputId, let target = targetInputId else { return }
        do {
            if needsUserProfile, let profile = buildProfile() {
                try FileManager.default.createDirectory(at: services.paths.userProfilesDirectory, withIntermediateDirectories: true)
                let path = services.paths.userProfilesDirectory.appendingPathComponent("\(profile.id).json")
                try Data(ProfileCatalog.serialize(profile).utf8).write(to: path, options: .atomic)
                services.log.info("Wizard saved user profile \(path.path).")
                services.reloadProfiles()
            }

            let label = targetLabel.trimmingCharacters(in: .whitespaces)
            let chosenHotkey = hotkey
            try services.settings.update { s in
                let ms = s.getOrAddMonitor(monitor.stableId)
                ms.localInputId = local
                ms.toggleInputs = [local, target]
                if !label.isEmpty {
                    ms.inputLabels[target] = label
                }
                if let chosenHotkey {
                    let keys = chosenHotkey.storageString
                    s.hotkeys.removeAll {
                        $0.keys.caseInsensitiveCompare(keys) == .orderedSame
                            || ($0.monitorId == monitor.stableId && $0.action == .switchInput && $0.inputId == target)
                    }
                    s.hotkeys.append(HotkeyBinding(monitorId: monitor.stableId, action: .switchInput, inputId: target, keys: keys))
                }
            }
            Task {
                await model.afterWizardSaved()
                model.windows.close(id: "wizard")
            }
        } catch {
            saveError = "Could not save: \(error.localizedDescription)"
        }
    }
}

struct SetupWizardView: View {
    @ObservedObject var wizard: WizardModel

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(wizard.title).font(.title2)
            content
            Spacer(minLength: 8)
            HStack {
                Spacer()
                buttons
            }
        }
        .padding(24)
        .frame(width: 560, alignment: .leading)
        .frame(minHeight: 320)
    }

    @ViewBuilder
    private var content: some View {
        switch wizard.page {
        case .noMonitors:
            wrapped("No monitor that supports DDC/CI was found.")
            wrapped("\(DdcHints.ddcCiDisabled) \(DdcHints.unsupportedPort)").foregroundStyle(.secondary)
            wrapped("For details, run 'monitorswitcher --diagnose' in Terminal.").foregroundStyle(.secondary)

        case .chooseMonitor:
            Text("Which monitor do you want to set up?")
            Picker("Monitor", selection: $wizard.chosenMonitorId) {
                ForEach(wizard.model.monitors, id: \.stableId) { m in
                    Text("\(wizard.model.label(for: m))   (\(m.stableId))").tag(m.stableId)
                }
            }
            .pickerStyle(.radioGroup)
            .labelsHidden()

        case .inputs:
            wrapped(wizard.introText)
            Grid(alignment: .leading, verticalSpacing: 8) {
                GridRow {
                    Text("This Mac is plugged into")
                    portPicker($wizard.localInputId)
                }
                GridRow {
                    Text("Test switching to")
                    portPicker($wizard.targetInputId)
                }
                GridRow {
                    Text("Your name for that input (optional)")
                    TextField("Name", text: $wizard.targetLabel, prompt: Text("e.g. PC")).labelsHidden().frame(width: 220)
                }
            }
            .padding(.vertical, 6)
            Text("How the test works").font(.headline)
            wrapped("1. The monitor switches to the input you chose for \(WizardModel.countdownSeconds) seconds. Make sure the device on that input is on, so you can see whether it worked.")
            wrapped("2. Monitor Switcher then switches it back to this Mac.")
            wrapped("3. If the screen stays on the other input, use the monitor's own buttons to come back. Some monitors only obey the computer they are showing; the wizard detects this and waits for you.")
                .foregroundStyle(.secondary)
            if let status = wizard.inputsStatus {
                wrapped(status).foregroundStyle(.red)
            }

        case .testing:
            Text(wizard.testStatus).font(.title)
            wrapped(wizard.testDetail).foregroundStyle(.secondary)
            wrapped(wizard.testHint)

        case .question:
            Text("What did the screen do?")
            if wizard.wentSilent {
                wrapped("The monitor stopped answering this Mac during the test, which usually means it switched.").foregroundStyle(.secondary)
            }
            radio("It switched to \(wizard.portName(wizard.targetInputId)): the other device appeared, or the screen stayed black or showed \"No signal\" for a few seconds",
                  selected: wizard.answer == .switched) { wizard.answer = .switched }
            radio("It only blinked (black for about a second), then this Mac's picture came straight back",
                  selected: wizard.answer == .blinked) { wizard.answer = .blinked }
            radio("Nothing happened", selected: wizard.answer == .nothing) { wizard.answer = .nothing }
            if wizard.asksHowItCameBack {
                Text("How did the screen come back to this Mac?").font(.headline).padding(.top, 8)
                radio("By itself", selected: wizard.howBack == .byItself) { wizard.howBack = .byItself }
                radio("I used the monitor's buttons", selected: wizard.howBack == .buttons) { wizard.howBack = .buttons }
            }
            if let status = wizard.questionStatus {
                wrapped(status).foregroundStyle(.red)
            }

        case .done:
            Text("✓  \(wizard.portName(wizard.targetInputId)) works").font(.title).foregroundStyle(.green)
            wrapped(wizard.resultSummary).foregroundStyle(.secondary)
            if wizard.showsQuirkNote {
                wrapped("This monitor only obeys the computer it is showing, so this Mac can switch it away but not back. Install Monitor Switcher on the other computer to switch back, or use the monitor's buttons.")
            }
            HStack {
                Text("Shortcut for \(wizard.targetLabel.isEmpty ? wizard.portName(wizard.targetInputId) : wizard.targetLabel)")
                KeyRecorder(hotkey: $wizard.hotkey).frame(width: 150, height: 22)
            }
            .padding(.top, 6)
            wrapped(wizard.needsUserProfile
                    ? "Saving creates a profile for this monitor in your Monitor Switcher folder."
                    : "These codes match the built-in profile, so only your settings change.")
                .foregroundStyle(.secondary)
            Button("Copy profile to share") { wizard.copyProfileToPasteboard() }
                .help("Share it at github.com/ashrash/monitor-control so it can ship built in.")
            if let error = wizard.saveError {
                wrapped(error).foregroundStyle(.red)
            }

        case .failure:
            wrapped("The wizard tried \(wizard.attempts.count) code(s) and none of them switched the monitor.")
            Text("Things to check").font(.headline)
            wrapped("• DDC/CI is turned on in the monitor's on-screen menu.")
            wrapped("• The Mac is connected over USB-C or DisplayPort. \(DdcHints.unsupportedPort)")
            wrapped("• The cable, dock or adapter passes DDC/CI; try a direct cable.")
            wrapped("• The device on the other input is switched on.")
            wrapped("• Run 'monitorswitcher --diagnose' and open an issue at github.com/ashrash/monitor-control with its output.")
                .foregroundStyle(.secondary)

        case .error(let message):
            wrapped(message)
        }
    }

    @ViewBuilder
    private var buttons: some View {
        switch wizard.page {
        case .noMonitors:
            Button("Close") { wizard.model.windows.close(id: "wizard") }.keyboardShortcut(.defaultAction)
        case .chooseMonitor:
            Button("Cancel") { wizard.model.windows.close(id: "wizard") }.keyboardShortcut(.cancelAction)
            Button("Next") { wizard.chooseMonitor() }.keyboardShortcut(.defaultAction)
        case .inputs:
            Button("Cancel") { wizard.model.windows.close(id: "wizard") }.keyboardShortcut(.cancelAction)
            Button("Start test") { wizard.startTest() }.keyboardShortcut(.defaultAction)
        case .testing:
            Button("Stop") { wizard.requestStop() }
            Button("It switched") { wizard.endCountdownEarly() }.keyboardShortcut(.defaultAction)
        case .question:
            Button("Cancel") { wizard.model.windows.close(id: "wizard") }.keyboardShortcut(.cancelAction)
            Button("Next") { wizard.submitAnswer() }.keyboardShortcut(.defaultAction)
        case .done:
            Button("Cancel") { wizard.model.windows.close(id: "wizard") }.keyboardShortcut(.cancelAction)
            Button("Save") { wizard.save() }.keyboardShortcut(.defaultAction)
        case .failure:
            Button("Close") { wizard.model.windows.close(id: "wizard") }
            Button("Try another input") {
                if let monitor = wizard.monitor { wizard.showInputs(monitor) }
            }
            .keyboardShortcut(.defaultAction)
        case .error:
            Button("Close") { wizard.model.windows.close(id: "wizard") }
            Button("Back") {
                if let monitor = wizard.monitor { wizard.showInputs(monitor) }
            }
            .keyboardShortcut(.defaultAction)
        }
    }

    private func wrapped(_ text: String) -> some View {
        Text(text).fixedSize(horizontal: false, vertical: true)
    }

    private func portPicker(_ selection: Binding<String?>) -> some View {
        Picker("", selection: selection) {
            Text("Choose…").tag(String?.none)
            ForEach(wizard.ports) { port in
                Text(port.name).tag(String?.some(port.inputId))
            }
        }
        .labelsHidden()
        .frame(width: 220)
    }

    private func radio(_ text: String, selected: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Image(systemName: selected ? "largecircle.fill.circle" : "circle")
                Text(text).fixedSize(horizontal: false, vertical: true).multilineTextAlignment(.leading)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}
