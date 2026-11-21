// monitorswitcher: switch a monitor's input over DDC/CI from the terminal.
// Same flags, output and exit codes as the Windows CLI.
import Foundation
import MonitorSwitcherCore
import MonitorSwitcherMac

enum ExitCodes {
    static let ok: Int32 = 0
    static let error: Int32 = 1
    static let usage: Int32 = 2
    static let monitorNotFound: Int32 = 3
    static let noTransport: Int32 = 4
    static let notResponding: Int32 = 5
    static let unknownInput: Int32 = 6
    static let notConfigured: Int32 = 7
    static let safety: Int32 = 8

    static func code(for kind: DdcErrorKind) -> Int32 {
        switch kind {
        case .monitorNotFound, .ambiguousMonitor: return monitorNotFound
        case .noSupportedTransport, .driverMissing, .unsupportedByTransport: return noTransport
        case .monitorNotResponding, .transportFailure, .monitorOnOtherInput: return notResponding
        case .inputNotInProfile: return unknownInput
        case .configuration: return notConfigured
        case .safetyViolation: return safety
        }
    }
}

let appVersion = (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String) ?? "0.1.0"

func printError(_ text: String) {
    FileHandle.standardError.write(Data((text + "\n").utf8))
}

final class ConsoleLog: LogSink {
    func write(_ level: LogLevel, _ message: String, _ error: Error?) {
        printError(error.map { "[\(level.label.lowercased())] \(message) (\($0))" } ?? "[\(level.label.lowercased())] \(message)")
    }
}

func printHelp() {
    print("""
    Monitor Switcher \(appVersion) - switch a monitor's input over DDC/CI.

    Usage:
      monitorswitcher --input <input> [--monitor <monitor>]
          Switch to an input. <input> is an id (usbc), a port name ("USB-C")
          or your own label (PC). Unique prefixes work too.
      monitorswitcher --toggle [<a>,<b>] [--monitor <monitor>]
          Switch between two inputs (from Settings unless given).
      monitorswitcher --list
          Show monitors, their profiles and inputs.
      monitorswitcher --diagnose
          Check DDC/CI access and profiles.
      monitorswitcher --read-vcp <code> [--monitor <monitor>]
          Read a VCP value (read-only), e.g. --read-vcp 0x60. For profile authors.

    Options:
      -m, --monitor   Number from --list, name, label or id. Needed only when
                      several monitors are connected and no default is set.
      -q, --quiet     Print nothing on success.
      -v, --verbose   Show debug output.
          --version   Print the version.

    Exit codes:
      0 ok, 1 error, 2 bad arguments, 3 monitor not found or ambiguous,
      4 no supported interface, 5 monitor did not accept the command,
      6 unknown input, 7 not configured, 8 blocked by a safety check.
    """)
}

enum Commands {
    static func label(_ m: ResolvedMonitor, _ settings: AppSettings) -> String {
        settings.findMonitor(m.stableId)?.label ?? m.name
    }

    static func describe(_ inputId: String, _ inputName: String, _ ms: MonitorSettings?) -> String {
        ms?.labelFor(inputId).map { "\(inputName) (\"\($0)\")" } ?? inputName
    }

    static func originText(_ m: ResolvedMonitor) -> String {
        if m.profile.origin == .fallback { return "no profile matched; standard MCCS guess" }
        let origin = m.profile.origin == .builtIn ? "built-in" : "user"
        let how: String
        switch m.quality {
        case .productCode: how = "product code"
        case .capabilitiesModel: how = "DDC model"
        case .modelName: how = "EDID name"
        default: how = "\(m.quality)"
        }
        return "\(origin), matched by \(how)"
    }

    static func switchInput(_ services: AppServices, _ monitorQuery: String?, _ inputQuery: String, quiet: Bool) throws -> Int32 {
        let settings = services.settings.load()
        let monitor = try MonitorResolver.resolve(try services.switcher.discoverMonitors(settings: settings), settings, monitorQuery)
        let ms = settings.findMonitor(monitor.stableId)
        let input = try InputResolver.resolve(monitor.profile, ms, inputQuery)
        let outcome = try services.switcher.switchTo(monitor, inputId: input.id, settings: ms)
        if !quiet {
            print("Switched \(label(monitor, settings)) to \(describe(outcome.inputId, outcome.inputName, ms)) via \(outcome.endpoint.transport.id) in \(Int(outcome.elapsed * 1000)) ms.")
        }
        return ExitCodes.ok
    }

    static func toggle(_ services: AppServices, _ monitorQuery: String?, _ pair: String?, quiet: Bool) throws -> Int32 {
        let settings = services.settings.load()
        let monitor = try MonitorResolver.resolve(try services.switcher.discoverMonitors(settings: settings), settings, monitorQuery)
        let ms = settings.findMonitor(monitor.stableId)

        let target: String
        if let pair {
            let parts = pair.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
            guard parts.count == 2 else {
                throw DdcError(.configuration, "--toggle takes two inputs separated by a comma, for example: --toggle hdmi2,usbc")
            }
            let a = try InputResolver.resolve(monitor.profile, ms, parts[0]).id
            let b = try InputResolver.resolve(monitor.profile, ms, parts[1]).id
            let last = services.state.load().monitors[monitor.stableId]?.lastInputId
            target = ToggleLogic.nextInput(a, b, lastInputId: last, localInputId: ms?.localInputId,
                                           ignoresCommandsWhenInactive: monitor.profile.effectiveQuirks.ignoresCommandsWhenInactive)
        } else {
            target = try services.switcher.toggleTarget(monitor, settings: ms)
        }

        let outcome = try services.switcher.switchTo(monitor, inputId: target, settings: ms)
        if !quiet {
            print("Switched \(label(monitor, settings)) to \(describe(outcome.inputId, outcome.inputName, ms)) via \(outcome.endpoint.transport.id) in \(Int(outcome.elapsed * 1000)) ms.")
        }
        return ExitCodes.ok
    }

    static func readVcp(_ services: AppServices, _ monitorQuery: String?, _ code: UInt8) throws -> Int32 {
        let settings = services.settings.load()
        let monitor = try MonitorResolver.resolve(try services.switcher.discoverMonitors(settings: settings, readCapabilities: false), settings, monitorQuery)
        for endpoint in monitor.endpoints {
            if let reading = endpoint.transport.readVcp(endpoint, code: code) {
                print("VCP \(Hex.byte(code)) via \(endpoint.transport.id): current 0x\(Hex.pad(reading.current, 4)) (\(reading.current)), maximum 0x\(Hex.pad(reading.maximum, 4)) (\(reading.maximum))")
                return ExitCodes.ok
            }
        }
        print("VCP \(Hex.byte(code)): no answer through \(monitor.endpoints.map { $0.transport.id }.joined(separator: ", ")).")
        return ExitCodes.notResponding
    }

    static func list(_ services: AppServices) throws -> Int32 {
        let settings = services.settings.load()
        let monitors = try services.switcher.discoverMonitors(settings: settings)
        let state = services.state.load()
        if monitors.isEmpty {
            print("No DDC/CI monitors found. Run 'monitorswitcher --diagnose' for details.")
            return ExitCodes.monitorNotFound
        }
        for (index, m) in monitors.enumerated() {
            printMonitor(index + 1, m, settings.findMonitor(m.stableId), state.monitors[m.stableId], settings)
        }
        return ExitCodes.ok
    }

    static func printMonitor(_ number: Int, _ m: ResolvedMonitor, _ ms: MonitorSettings?, _ state: MonitorState?, _ settings: AppSettings) {
        let id = m.identity
        print("\(number)) \(label(m, settings))   [\(m.stableId)]")
        print("   Display:   \(m.osDisplayName ?? "(not listed by macOS)")\(m.isRestoredFromCache ? "  - using last known address" : "")")
        print("   EDID:      \(id.manufacturerId) 0x\(Hex.pad(id.productCode, 4)) \"\(id.modelName ?? "")\", serial \(id.serialNumber)")
        if let model = m.capabilities?.model {
            print("   DDC model: \(model)")
        }
        print("   Profile:   \(m.profile.id) (\(originText(m))), protocol \(m.profile.protocolId)")
        print("   Reach:     \(m.endpoints.map { "\($0.transport.id) (\($0.connectorId))" }.joined(separator: "; "))")
        let sender = m.endpoint(for: m.inputProtocol)
        print("   Sends via: \(sender?.transport.displayName ?? "nothing - no transport can send this protocol")")
        print("   Inputs:")
        for input in m.profile.inputs {
            var flags: [String] = []
            if input.verified { flags.append("verified") }
            if state?.lastInputId?.caseInsensitiveCompare(input.id) == .orderedSame { flags.append("last sent") }
            if ms?.localInputId?.caseInsensitiveCompare(input.id) == .orderedSame { flags.append("this Mac") }
            let labelText = ms?.labelFor(input.id).map { "\"\($0)\"  " } ?? ""
            print("     \(input.id.padding(toLength: 8, withPad: " ", startingAt: 0)) \(input.name.padding(toLength: 14, withPad: " ", startingAt: 0)) \(input.code.padding(toLength: 6, withPad: " ", startingAt: 0)) \(labelText)\(flags.joined(separator: ", "))")
        }
        if let pair = ToggleLogic.toggleInputs(m.profile, ms) {
            print("   Toggle:    \(pair.a) <-> \(pair.b)")
        } else {
            print("   Toggle:    not set (choose two inputs in Settings)")
        }
        if m.profile.effectiveQuirks.ignoresCommandsWhenInactive {
            print("   Note:      this monitor only obeys the computer it is showing; each computer can switch it away, not back.")
        }
        if m.needsSetup {
            print("   No profile matched this monitor; these are standard MCCS guesses. Run the setup wizard to confirm them.")
        }
        print("")
    }

    static func diagnose(_ services: AppServices) throws -> Int32 {
        print("Monitor Switcher \(appVersion) diagnostics")
        print("Data folder: \(services.paths.root.path)")
        print("Log file:    \(services.paths.logsDirectory.appendingPathComponent("monitorswitcher.log").path)")
        print("")

        print("Interfaces:")
        let reports = services.switcher.probeTransports()
        for r in reports {
            print("  [\(r.status.isAvailable ? "ok" : "--")] \(r.transport.id.padding(toLength: 11, withPad: " ", startingAt: 0)) \(r.transport.displayName): \(r.status.summary)")
        }
        print("")

        let builtIn = services.profiles.profiles.filter { $0.origin == .builtIn }.count
        let user = services.profiles.profiles.filter { $0.origin == .user }.count
        print("Profiles: \(builtIn) built-in, \(user) from \(services.paths.userProfilesDirectory.path)")
        for error in services.profiles.errors {
            print("  NOT LOADED \(error.source): \(error.message)")
        }
        print("")

        guard reports.contains(where: { $0.status.isAvailable }) else {
            print("No DDC/CI interface is available on this Mac.")
            return ExitCodes.noTransport
        }

        let settings = services.settings.load()
        let monitors = try services.switcher.discoverMonitors(settings: settings)
        print("Monitors: \(monitors.count)")
        var problems = 0
        for (index, m) in monitors.enumerated() {
            print("  \(index + 1)) \(label(m, settings)) [\(m.stableId)] - profile \(m.profile.id) (\(originText(m)))")
            if let sender = m.endpoint(for: m.inputProtocol) {
                print("     Switching uses \(sender.transport.displayName) (\(sender.connectorId)).")
            } else {
                problems += 1
                print("     PROBLEM: no interface can send \(m.profile.protocolId) to this monitor.")
            }
            // A write can "succeed" even if the monitor ignores it, so ask the monitor to answer.
            if let endpoint = m.endpoints.first(where: { $0.transport.supportsReads }) {
                if let reading = endpoint.transport.readVcp(endpoint, code: 0x10) {
                    print("     DDC/CI read via \(endpoint.transport.id): OK (brightness \(reading.current)/\(reading.maximum)).")
                } else {
                    problems += 1
                    print("     PROBLEM: the monitor did not answer a DDC/CI read. \(DdcHints.ddcCiDisabled) \(DdcHints.unsupportedPort)")
                }
            }
            if m.needsSetup {
                print("     No profile matched; run the setup wizard to find working input codes.")
            }
        }
        print("")
        print(problems == 0 ? "No problems found." : "\(problems) problem(s) found.")
        return problems == 0 ? ExitCodes.ok : ExitCodes.notResponding
    }
}

// MARK: - Entry point

let options: Options
do {
    options = try Options.parse(Array(CommandLine.arguments.dropFirst()))
} catch let error as UsageError {
    printError(error.message)
    printError("Run 'monitorswitcher --help' for usage.")
    exit(ExitCodes.usage)
} catch {
    printError("\(error)")
    exit(ExitCodes.usage)
}

if options.version {
    print("monitorswitcher \(appVersion)")
    exit(ExitCodes.ok)
}
if options.help {
    printHelp()
    exit(ExitCodes.ok)
}

let services = MacPlatform.create(processTag: "cli", extraLog: options.verbose ? ConsoleLog() : nil,
                                  fileLogLevel: options.verbose ? .debug : .info)
do {
    let code: Int32
    switch options.command {
    case .list: code = try Commands.list(services)
    case .diagnose: code = try Commands.diagnose(services)
    case .input: code = try Commands.switchInput(services, options.monitor, options.input ?? "", quiet: options.quiet)
    case .toggle: code = try Commands.toggle(services, options.monitor, options.togglePair, quiet: options.quiet)
    case .readVcp: code = try Commands.readVcp(services, options.monitor, options.vcpCode)
    case .none: code = ExitCodes.usage
    }
    exit(code)
} catch let error as DdcError {
    services.log.error("CLI: \(error.message)", error.kind == .safetyViolation ? error : nil)
    printError("Error: \(error.userMessage)")
    exit(ExitCodes.code(for: error.kind))
} catch {
    services.log.error("CLI: unexpected error.", error)
    printError("Unexpected error: \(error)")
    printError("Details are in \(services.paths.logsDirectory.appendingPathComponent("monitorswitcher.log").path)")
    exit(ExitCodes.error)
}
