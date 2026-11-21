using MonitorSwitcher.Core.Controls;

namespace MonitorSwitcher.Cli;

internal enum Command
{
    None,
    Input,
    Toggle,
    List,
    Diagnose,
    ReadVcp,
    Control,
    TestPeer,
}

internal sealed class UsageException(string message) : Exception(message);

internal sealed class Options
{
    public Command Command { get; private set; }

    public string? Input { get; private set; }

    public string? Monitor { get; private set; }

    /// <summary>Optional "a,b" pair for --toggle.</summary>
    public string? TogglePair { get; private set; }

    /// <summary>VCP code for --read-vcp.</summary>
    public byte VcpCode { get; private set; }

    /// <summary>Brightness or volume for --brightness / --volume.</summary>
    public MonitorControl Control { get; private set; }

    /// <summary>Null with <see cref="Command.Control"/> means "print the current value".</summary>
    public ControlChange? ControlChange { get; private set; }

    public bool Verbose { get; private set; }

    public bool Quiet { get; private set; }

    public bool Help { get; private set; }

    public bool Version { get; private set; }

    public static Options Parse(IReadOnlyList<string> args)
    {
        var options = new Options();
        for (int i = 0; i < args.Count; i++)
        {
            var (name, inlineValue) = Split(args[i]);
            switch (name)
            {
                case "--input" or "-i":
                    options.SetCommand(Command.Input);
                    options.Input = inlineValue ?? Next(args, ref i, name);
                    break;
                case "--monitor" or "-m":
                    options.Monitor = inlineValue ?? Next(args, ref i, name);
                    break;
                case "--toggle" or "-t":
                    options.SetCommand(Command.Toggle);
                    options.TogglePair = inlineValue ?? (i + 1 < args.Count && !args[i + 1].StartsWith('-') ? args[++i] : null);
                    break;
                case "--list" or "-l":
                    options.SetCommand(Command.List);
                    break;
                case "--diagnose":
                    options.SetCommand(Command.Diagnose);
                    break;
                case "--read-vcp":
                    options.SetCommand(Command.ReadVcp);
                    var code = inlineValue ?? Next(args, ref i, name);
                    if (!Core.Hex.TryParseUInt16(code, out var vcp) || vcp > 0xFF)
                    {
                        throw new UsageException($"--read-vcp needs a VCP code such as 0x60, not '{code}'.");
                    }
                    options.VcpCode = (byte)vcp;
                    break;
                case "--brightness" or "-b" or "--volume":
                    options.SetCommand(Command.Control);
                    options.Control = name == "--volume" ? MonitorControl.Volume : MonitorControl.Brightness;
                    var text = inlineValue ?? (i + 1 < args.Count && LooksLikeLevel(args[i + 1]) ? args[++i] : null);
                    if (text is not null)
                    {
                        if (!Core.Controls.ControlChange.TryParse(text, out var change))
                        {
                            throw new UsageException($"{name} takes 0-100, or +N / -N to step, not '{text}'.");
                        }
                        options.ControlChange = change;
                    }
                    break;
                case "--test-peer":
                    options.SetCommand(Command.TestPeer);
                    break;
                case "--verbose" or "-v":
                    options.Verbose = true;
                    break;
                case "--quiet" or "-q":
                    options.Quiet = true;
                    break;
                case "--help" or "-h" or "-?" or "/?":
                    options.Help = true;
                    break;
                case "--version":
                    options.Version = true;
                    break;
                default:
                    throw new UsageException($"Unknown option '{args[i]}'.");
            }
        }

        if (options.Command == Command.None && !options.Help && !options.Version)
        {
            options.Help = true;
        }
        if (options.Command == Command.Input && string.IsNullOrWhiteSpace(options.Input))
        {
            throw new UsageException("--input needs an input, for example: --input usbc");
        }
        return options;
    }

    private void SetCommand(Command command)
    {
        if (Command != Command.None && Command != command)
        {
            throw new UsageException("Use only one of --input, --toggle, --brightness, --volume, --list, --diagnose, --test-peer or --read-vcp.");
        }
        Command = command;
    }

    /// <summary>"70", "70%", "+10", "-10" (so "-10" is a value, not an option).</summary>
    private static bool LooksLikeLevel(string arg) =>
        System.Text.RegularExpressions.Regex.IsMatch(arg, @"^[+-]?\d+%?$");

    private static (string Name, string? Value) Split(string arg)
    {
        if (arg.StartsWith("--", StringComparison.Ordinal) && arg.IndexOf('=', StringComparison.Ordinal) is var eq and > 2)
        {
            return (arg[..eq].ToLowerInvariant(), arg[(eq + 1)..]);
        }
        return (arg.ToLowerInvariant(), null);
    }

    private static string Next(IReadOnlyList<string> args, ref int i, string name)
    {
        if (i + 1 >= args.Count)
        {
            throw new UsageException($"{name} needs a value.");
        }
        return args[++i];
    }
}
