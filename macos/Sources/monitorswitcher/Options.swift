import Foundation
import MonitorSwitcherCore

enum Command {
    case none, input, toggle, list, diagnose, readVcp
}

struct UsageError: Error {
    let message: String
}

/// Same flags as the Windows CLI.
struct Options {
    var command: Command = .none
    var input: String?
    var monitor: String?
    var togglePair: String?
    var vcpCode: UInt8 = 0
    var verbose = false
    var quiet = false
    var help = false
    var version = false

    static func parse(_ args: [String]) throws -> Options {
        var options = Options()
        var i = 0
        func next(_ name: String) throws -> String {
            guard i + 1 < args.count else { throw UsageError(message: "\(name) needs a value.") }
            i += 1
            return args[i]
        }

        while i < args.count {
            let (name, inline) = split(args[i])
            switch name {
            case "--input", "-i":
                try options.set(.input)
                options.input = try inline ?? next(name)
            case "--monitor", "-m":
                options.monitor = try inline ?? next(name)
            case "--toggle", "-t":
                try options.set(.toggle)
                if let inline {
                    options.togglePair = inline
                } else if i + 1 < args.count, !args[i + 1].hasPrefix("-") {
                    i += 1
                    options.togglePair = args[i]
                }
            case "--list", "-l":
                try options.set(.list)
            case "--diagnose":
                try options.set(.diagnose)
            case "--read-vcp":
                try options.set(.readVcp)
                let code = try inline ?? next(name)
                guard let value = Hex.parseUInt16(code), value <= 0xFF else {
                    throw UsageError(message: "--read-vcp needs a VCP code such as 0x60, not '\(code)'.")
                }
                options.vcpCode = UInt8(value)
            case "--verbose", "-v":
                options.verbose = true
            case "--quiet", "-q":
                options.quiet = true
            case "--help", "-h", "-?":
                options.help = true
            case "--version":
                options.version = true
            default:
                throw UsageError(message: "Unknown option '\(args[i])'.")
            }
            i += 1
        }

        if options.command == .none && !options.help && !options.version {
            options.help = true
        }
        if options.command == .input && (options.input?.trimmingCharacters(in: .whitespaces).isEmpty ?? true) {
            throw UsageError(message: "--input needs an input, for example: --input usbc")
        }
        return options
    }

    private mutating func set(_ command: Command) throws {
        if self.command != .none && self.command != command {
            throw UsageError(message: "Use only one of --input, --toggle, --list, --diagnose or --read-vcp.")
        }
        self.command = command
    }

    private static func split(_ arg: String) -> (String, String?) {
        if arg.hasPrefix("--"), let eq = arg.firstIndex(of: "="), arg.distance(from: arg.startIndex, to: eq) > 2 {
            return (String(arg[..<eq]).lowercased(), String(arg[arg.index(after: eq)...]))
        }
        return (arg.lowercased(), nil)
    }
}
