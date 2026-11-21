using System.Reflection;
using MonitorSwitcher.Core;
using MonitorSwitcher.Core.Controls;
using MonitorSwitcher.Core.Ddc;
using MonitorSwitcher.Core.Logging;
using MonitorSwitcher.Core.Peer;
using MonitorSwitcher.Core.Profiles;
using MonitorSwitcher.Core.Storage;
using MonitorSwitcher.Core.Switching;
using MonitorSwitcher.Windows;
using MonitorSwitcher.Windows.Peer;

namespace MonitorSwitcher.Cli;

internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int Usage = 2;
    public const int MonitorNotFound = 3;
    public const int NoTransport = 4;
    public const int NotResponding = 5;
    public const int UnknownInput = 6;
    public const int NotConfigured = 7;
    public const int Safety = 8;

    public static int For(DdcErrorKind kind) => kind switch
    {
        DdcErrorKind.MonitorNotFound or DdcErrorKind.AmbiguousMonitor => MonitorNotFound,
        DdcErrorKind.NoSupportedTransport or DdcErrorKind.DriverMissing or DdcErrorKind.UnsupportedByTransport => NoTransport,
        DdcErrorKind.MonitorNotResponding or DdcErrorKind.TransportFailure or DdcErrorKind.MonitorOnOtherInput
            or DdcErrorKind.PeerUnavailable => NotResponding,
        DdcErrorKind.InputNotInProfile => UnknownInput,
        DdcErrorKind.Configuration => NotConfigured,
        DdcErrorKind.SafetyViolation => Safety,
        _ => Error,
    };
}

internal static class Program
{
    private static string AppVersion =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private static int Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Run 'monitorswitcher --help' for usage.");
            return ExitCodes.Usage;
        }

        if (options.Version)
        {
            Console.WriteLine($"monitorswitcher {AppVersion}");
            return ExitCodes.Ok;
        }
        if (options.Help)
        {
            PrintHelp();
            return ExitCodes.Ok;
        }

        using var services = WindowsPlatform.Create(
            "cli",
            options.Verbose ? new ConsoleLog() : null,
            options.Verbose ? LogLevel.Debug : LogLevel.Info);

        try
        {
            return options.Command switch
            {
                Command.List => Commands.List(services),
                Command.Diagnose => Commands.Diagnose(services, AppVersion),
                Command.Input => Commands.Switch(services, options.Monitor, options.Input!, options.Quiet),
                Command.Toggle => Commands.Toggle(services, options.Monitor, options.TogglePair, options.Quiet),
                Command.ReadVcp => Commands.ReadVcp(services, options.Monitor, options.VcpCode),
                Command.Control => Commands.Control(services, options.Monitor, options.Control, options.ControlChange, options.Quiet),
                Command.TestPeer => Commands.TestPeer(services),
                _ => ExitCodes.Usage,
            };
        }
        catch (DdcException ex)
        {
            services.Log.Error($"CLI: {ex.Message}", ex.Kind == DdcErrorKind.SafetyViolation ? ex : null);
            Console.Error.WriteLine($"Error: {ex.UserMessage}");
            return ExitCodes.For(ex.Kind);
        }
        catch (Exception ex)
        {
            services.Log.Error("CLI: unexpected error.", ex);
            Console.Error.WriteLine($"Unexpected error: {ex.Message}");
            Console.Error.WriteLine($"Details are in {Path.Combine(services.Paths.LogsDirectory, "monitorswitcher.log")}");
            return ExitCodes.Error;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
            Monitor Switcher {AppVersion} - switch a monitor's input over DDC/CI.

            Usage:
              monitorswitcher --input <input> [--monitor <monitor>]
                  Switch to an input. <input> is an id (usbc), a port name ("USB-C")
                  or your own label (Mac). Unique prefixes work too.
              monitorswitcher --toggle [<a>,<b>] [--monitor <monitor>]
                  Switch between two inputs (from Settings unless given).
              monitorswitcher --brightness [<value>] [--monitor <monitor>]
              monitorswitcher --volume [<value>] [--monitor <monitor>]
                  Show or set brightness or speaker volume: 0-100, or +N / -N to step.
              monitorswitcher --list
                  Show monitors, their profiles and inputs.
              monitorswitcher --diagnose
                  Check GPU drivers, DDC/CI and profiles.
              monitorswitcher --test-peer
                  Check that the paired computer answers (Settings, Other computer).
              monitorswitcher --read-vcp <code> [--monitor <monitor>]
                  Read a VCP value (read-only), e.g. --read-vcp 0x60. For profile authors.

            If the monitor only obeys the computer it is showing and this computer is
            paired with the other one, --input, --toggle, --brightness and --volume ask
            the other computer to do it.

            Options:
              -m, --monitor   Number from --list, name, label or id. Needed only when
                              several monitors are connected and no default is set.
              -q, --quiet     Print nothing on success.
              -v, --verbose   Show debug output.
                  --version   Print the version.

            Exit codes:
              0 ok, 1 error, 2 bad arguments, 3 monitor not found or ambiguous,
              4 no supported GPU/driver, 5 monitor did not accept the command,
              6 unknown input, 7 not configured, 8 blocked by a safety check.
            """);
    }
}

internal sealed class ConsoleLog : ILog
{
    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        Console.Error.WriteLine(exception is null
            ? $"[{level.ToString().ToLowerInvariant()}] {message}"
            : $"[{level.ToString().ToLowerInvariant()}] {message} ({exception.Message})");
    }
}

internal static class Commands
{
    public static int Switch(AppServices services, string? monitorQuery, string inputQuery, bool quiet)
    {
        var settings = services.Settings.Load();
        var monitor = MonitorResolver.Resolve(services.Switcher.DiscoverMonitors(settings), settings, monitorQuery);
        var monitorSettings = settings.FindMonitor(monitor.StableId);
        var input = InputResolver.Resolve(monitor.Profile, monitorSettings, inputQuery);

        var result = PeerFactory.Actions(services, settings).SwitchInput(monitor, input.Id, monitorSettings);
        PrintResult(result, monitor, settings, monitorSettings, quiet);
        return ExitCodes.Ok;
    }

    public static int Toggle(AppServices services, string? monitorQuery, string? pair, bool quiet)
    {
        var settings = services.Settings.Load();
        var monitor = MonitorResolver.Resolve(services.Switcher.DiscoverMonitors(settings), settings, monitorQuery);
        var monitorSettings = settings.FindMonitor(monitor.StableId);

        (string A, string B)? explicitPair = null;
        if (pair is not null)
        {
            var parts = pair.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                throw new DdcException(DdcErrorKind.Configuration, "--toggle takes two inputs separated by a comma, for example: --toggle hdmi2,usbc");
            }
            explicitPair = (InputResolver.Resolve(monitor.Profile, monitorSettings, parts[0]).Id,
                InputResolver.Resolve(monitor.Profile, monitorSettings, parts[1]).Id);
        }

        var result = PeerFactory.Actions(services, settings).Toggle(monitor, monitorSettings, explicitPair);
        PrintResult(result, monitor, settings, monitorSettings, quiet);
        return ExitCodes.Ok;
    }

    public static int Control(AppServices services, string? monitorQuery, MonitorControl control, ControlChange? change, bool quiet)
    {
        var settings = services.Settings.Load();
        var monitor = MonitorResolver.Resolve(services.Switcher.DiscoverMonitors(settings), settings, monitorQuery);
        if (change is not { } requested)
        {
            var reading = services.Switcher.ReadControl(monitor, control) ?? throw new DdcException(
                DdcErrorKind.MonitorNotResponding,
                $"{Label(monitor, settings)} did not report its {control.Name().ToLowerInvariant()}.",
                monitor.Profile.EffectiveQuirks.IgnoresCommandsWhenInactive ? "It may be showing another input." : DdcHints.DdcCiDisabled);
            Console.WriteLine($"{control.Name()} of {Label(monitor, settings)}: {ControlMath.Percent(reading)}% ({reading.Current} of {reading.Maximum})");
            return ExitCodes.Ok;
        }

        var result = PeerFactory.Actions(services, settings).SetControl(monitor, control, requested);
        PrintResult(result, monitor, settings, settings.FindMonitor(monitor.StableId), quiet);
        return ExitCodes.Ok;
    }

    public static int TestPeer(AppServices services)
    {
        var settings = services.Settings.Load();
        if (string.IsNullOrWhiteSpace(settings.Peer?.PairingCode))
        {
            throw new DdcException(DdcErrorKind.Configuration, "This computer is not paired.",
                "Enter the same pairing code on both computers in Settings, Other computer.");
        }
        if (settings.Peer.Key is null)
        {
            throw new DdcException(DdcErrorKind.Configuration, "The pairing code in settings.json is not valid.",
                "Create a new one in Settings, Other computer.");
        }
        var reply = PeerFactory.Actions(services, settings).Ping();
        Console.WriteLine($"{reply.Message} ({services.State.Load().Peer?.LastAddress ?? "?"}, UDP port {settings.Peer.EffectivePort})");
        return ExitCodes.Ok;
    }

    private static void PrintResult(ActionResult result, ResolvedMonitor monitor, AppSettings settings, MonitorSettings? ms, bool quiet)
    {
        if (quiet)
        {
            return;
        }
        Console.WriteLine(result.Control is { } control
            ? $"{control.Name()} of {Label(monitor, settings)} is now {(result.Percent is { } p ? $"{p}%" : "changed")} {result.ViaText}."
            : $"Switched {Label(monitor, settings)} to {Describe(result.InputId ?? "", result.InputName ?? "", ms)} {result.ViaText}.");
    }

    public static int ReadVcp(AppServices services, string? monitorQuery, byte code)
    {
        var settings = services.Settings.Load();
        var monitor = MonitorResolver.Resolve(services.Switcher.DiscoverMonitors(settings, readCapabilities: false), settings, monitorQuery);
        foreach (var endpoint in monitor.Endpoints)
        {
            if (endpoint.Transport.TryReadVcp(endpoint, code) is { } reading)
            {
                Console.WriteLine($"VCP 0x{code:X2} via {endpoint.Transport.Id}: current 0x{reading.Current:X4} ({reading.Current}), maximum 0x{reading.Maximum:X4} ({reading.Maximum})");
                return ExitCodes.Ok;
            }
        }
        Console.WriteLine($"VCP 0x{code:X2}: no answer through {string.Join(", ", monitor.Endpoints.Select(e => e.Transport.Id))}.");
        return ExitCodes.NotResponding;
    }

    public static int List(AppServices services)
    {
        var settings = services.Settings.Load();
        var monitors = services.Switcher.DiscoverMonitors(settings);
        var state = services.State.Load();

        if (monitors.Count == 0)
        {
            Console.WriteLine("No DDC/CI monitors found. Run 'monitorswitcher --diagnose' for details.");
            return ExitCodes.MonitorNotFound;
        }

        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            var ms = settings.FindMonitor(m.StableId);
            state.Monitors.TryGetValue(m.StableId, out var monitorState);
            PrintMonitor(i + 1, m, ms, monitorState, settings);
        }
        return ExitCodes.Ok;
    }

    private static void PrintMonitor(int number, ResolvedMonitor m, MonitorSettings? ms, MonitorState? state, AppSettings settings)
    {
        var id = m.Identity;
        Console.WriteLine($"{number}) {Label(m, settings)}   [{m.StableId}]");
        Console.WriteLine($"   Display:   {m.OsDisplayName ?? "(not listed by Windows)"}{(m.IsRestoredFromCache ? "  - using last known address" : "")}");
        Console.WriteLine($"   EDID:      {id.ManufacturerId} 0x{id.ProductCode:X4} \"{id.ModelName}\", serial {id.SerialNumber}");
        if (m.Capabilities?.Model is { } capsModel)
        {
            Console.WriteLine($"   DDC model: {capsModel}");
        }
        Console.WriteLine($"   Profile:   {m.Profile.Id} ({OriginText(m)}), protocol {m.Profile.Protocol}");
        Console.WriteLine($"   Reach:     {string.Join("; ", m.Endpoints.Select(e => $"{e.Transport.Id} ({e.ConnectorId})"))}");
        var sender = m.EndpointFor(m.Protocol);
        Console.WriteLine($"   Sends via: {(sender is null ? "nothing - no transport can send this protocol" : sender.Transport.DisplayName)}");
        Console.WriteLine("   Inputs:");
        foreach (var input in m.Profile.Inputs)
        {
            var label = ms?.LabelFor(input.Id);
            var flags = new List<string>();
            if (input.Verified)
            {
                flags.Add("verified");
            }
            if (string.Equals(state?.LastInputId, input.Id, StringComparison.OrdinalIgnoreCase))
            {
                flags.Add("last sent");
            }
            if (string.Equals(ms?.LocalInputId, input.Id, StringComparison.OrdinalIgnoreCase))
            {
                flags.Add("this PC");
            }
            Console.WriteLine($"     {input.Id,-8} {input.Name,-14} {input.Code,-6} {(label is null ? "" : $"\"{label}\"  ")}{string.Join(", ", flags)}");
        }

        var pair = ToggleLogic.ToggleInputs(m.Profile, ms);
        Console.WriteLine(pair is { } p ? $"   Toggle:    {p.A} <-> {p.B}" : "   Toggle:    not set (choose two inputs in Settings)");
        if (m.Profile.EffectiveQuirks.IgnoresCommandsWhenInactive)
        {
            Console.WriteLine("   Note:      this monitor only obeys the computer it is showing; each computer can switch it away, not back, unless the computers are paired.");
        }
        if (m.NeedsSetup)
        {
            Console.WriteLine("   No profile matched this monitor; these are standard MCCS guesses. Run the setup wizard to confirm them.");
        }
        Console.WriteLine();
    }

    public static int Diagnose(AppServices services, string version)
    {
        Console.WriteLine($"Monitor Switcher {version} diagnostics");
        Console.WriteLine($"Data folder: {services.Paths.Root}");
        Console.WriteLine($"Log file:    {Path.Combine(services.Paths.LogsDirectory, "monitorswitcher.log")}");
        Console.WriteLine();

        Console.WriteLine("Driver interfaces:");
        var reports = services.Switcher.ProbeTransports();
        foreach (var r in reports)
        {
            Console.WriteLine($"  [{(r.Status.IsAvailable ? "ok" : "--")}] {r.Transport.Id,-6} {r.Transport.DisplayName}: {r.Status.Summary}");
        }
        Console.WriteLine();

        var builtIn = services.Profiles.Profiles.Count(p => p.Origin == ProfileOrigin.BuiltIn);
        var user = services.Profiles.Profiles.Count(p => p.Origin == ProfileOrigin.User);
        Console.WriteLine($"Profiles: {builtIn} built-in, {user} from {services.Paths.UserProfilesDirectory}");
        foreach (var error in services.Profiles.Errors)
        {
            Console.WriteLine($"  NOT LOADED {error.Source}: {error.Message}");
        }
        Console.WriteLine();

        if (!reports.Any(r => r.Status.IsAvailable))
        {
            Console.WriteLine($"No driver interface is available. {DdcHints.UpdateDriver}");
            return ExitCodes.NoTransport;
        }

        var settings = services.Settings.Load();
        var monitors = services.Switcher.DiscoverMonitors(settings);
        Console.WriteLine($"Monitors: {monitors.Count}");
        int problems = 0;
        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            Console.WriteLine($"  {i + 1}) {Label(m, settings)} [{m.StableId}] - profile {m.Profile.Id} ({OriginText(m)})");

            var sender = m.EndpointFor(m.Protocol);
            if (sender is null)
            {
                problems++;
                Console.WriteLine($"     PROBLEM: {m.Profile.Protocol} needs a raw I2C interface (NVIDIA, AMD or Intel driver) and none reaches this monitor.");
            }
            else
            {
                Console.WriteLine($"     Switching uses {sender.Transport.DisplayName} ({sender.ConnectorId}).");
            }

            // A write can "succeed" even if the monitor ignores it, so ask the monitor to answer.
            var answered = false;
            foreach (var endpoint in m.Endpoints)
            {
                var reading = endpoint.Transport.TryReadVcp(endpoint, 0x10);
                if (reading is { } r)
                {
                    answered = true;
                    Console.WriteLine($"     DDC/CI read via {endpoint.Transport.Id}: OK (brightness {r.Current}/{r.Maximum}).");
                    break;
                }
            }
            if (!answered)
            {
                if (m.Endpoints.Any(e => e.Transport.Id == "dxva2"))
                {
                    problems++;
                    Console.WriteLine($"     PROBLEM: the monitor did not answer a DDC/CI read. {DdcHints.DdcCiDisabled}");
                }
                else
                {
                    Console.WriteLine("     DDC/CI read: not available through these interfaces (not necessarily a problem).");
                }
            }

            if (m.NeedsSetup)
            {
                Console.WriteLine("     No profile matched; run the setup wizard to find working input codes.");
            }
        }

        Console.WriteLine();
        Console.WriteLine(problems == 0 ? "No problems found." : $"{problems} problem(s) found.");
        return problems == 0 ? ExitCodes.Ok : ExitCodes.NotResponding;
    }

    private static string Label(ResolvedMonitor m, AppSettings settings) =>
        settings.FindMonitor(m.StableId)?.Label ?? m.Name;

    private static string Describe(string inputId, string inputName, MonitorSettings? ms) =>
        ms?.LabelFor(inputId) is { } label ? $"{inputName} (\"{label}\")" : inputName;

    private static string OriginText(ResolvedMonitor m) => (m.Profile.Origin, m.Quality) switch
    {
        (ProfileOrigin.Fallback, _) => "no profile matched; standard MCCS guess",
        (var origin, var quality) => $"{(origin == ProfileOrigin.BuiltIn ? "built-in" : "user")}, matched by {quality switch
        {
            MatchQuality.ProductCode => "product code",
            MatchQuality.CapabilitiesModel => "DDC model",
            MatchQuality.ModelName => "EDID name",
            _ => quality.ToString(),
        }}",
    };
}
