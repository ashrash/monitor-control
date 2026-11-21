using MonitorSwitcher.Core.Switching;
using MonitorSwitcher.Core.Transports;

namespace MonitorSwitcher.Core.Controls;

/// <summary>
/// Picture and sound settings Monitor Switcher can change besides the input. Mirrors <c>Controls.swift</c>.
/// </summary>
/// <remarks>
/// These are the only VCP codes ever written outside input switching; <see cref="Ddc.DdcGuard.EnsureControlFrame"/>
/// enforces that, so a bug elsewhere cannot turn this path into a general "write any VCP code" tool.
/// </remarks>
public enum MonitorControl
{
    Brightness,
    Volume,
}

public static class MonitorControls
{
    public static readonly MonitorControl[] All = [MonitorControl.Brightness, MonitorControl.Volume];

    /// <summary>MCCS VCP code: 0x10 Luminance, 0x62 Audio speaker volume.</summary>
    public static byte VcpCode(this MonitorControl control) => control switch
    {
        MonitorControl.Brightness => 0x10,
        MonitorControl.Volume => 0x62,
        _ => throw new ArgumentOutOfRangeException(nameof(control)),
    };

    /// <summary>"Brightness" or "Volume"; also the name used in settings, state and peer messages.</summary>
    public static string Name(this MonitorControl control) => control.ToString();

    /// <summary>Case-insensitive lookup by name.</summary>
    public static MonitorControl? Named(string? name) =>
        All.Cast<MonitorControl?>().FirstOrDefault(c => string.Equals(c!.Value.Name(), name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A requested change: an absolute percentage, or a step up or down from the current value.</summary>
public readonly record struct ControlChange(bool IsRelative, int Value)
{
    /// <summary>Default step for "brighter", "louder" and so on.</summary>
    public const int Step = 10;

    public static ControlChange Set(int percent) => new(false, percent);

    public static ControlChange Adjust(int delta) => new(true, delta);

    /// <summary>"70", "70%", "+10", "-10".</summary>
    public static bool TryParse(string? text, out ControlChange change)
    {
        change = default;
        var t = (text ?? "").Trim();
        if (t.EndsWith('%'))
        {
            t = t[..^1];
        }
        if (t.Length == 0 || !t.All(c => char.IsAsciiDigit(c) || c is '+' or '-') || t.Skip(1).Any(c => c is '+' or '-'))
        {
            return false;
        }
        if (!int.TryParse(t, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }
        if (t[0] is '+' or '-')
        {
            if (Math.Abs(value) > 100)
            {
                return false;
            }
            change = Adjust(value);
            return true;
        }
        if (value is < 0 or > 100)
        {
            return false;
        }
        change = Set(value);
        return true;
    }

    /// <summary>The percentage after applying this change to <paramref name="current"/> (0-100).</summary>
    public int? ApplyTo(int? current) => IsRelative
        ? current is { } c ? ControlMath.Clamp(c + Value) : null
        : ControlMath.Clamp(Value);

    public override string ToString() => IsRelative ? (Value >= 0 ? $"+{Value}%" : $"{Value}%") : $"{Value}%";
}

/// <summary>
/// Converts between percentages and the monitor's own range. Monitors report their maximum
/// (usually 100); a maximum of 0 is treated as 100.
/// </summary>
public static class ControlMath
{
    public static int Clamp(int percent) => Math.Clamp(percent, 0, 100);

    public static int Percent(VcpReading reading)
    {
        var maximum = EffectiveMaximum(reading.Maximum);
        return Clamp((int)Math.Round(Math.Min(reading.Current, maximum) * 100.0 / maximum, MidpointRounding.AwayFromZero));
    }

    public static ushort Value(int percent, ushort maximum) =>
        (ushort)Math.Round(Clamp(percent) * (double)EffectiveMaximum(maximum) / 100, MidpointRounding.AwayFromZero);

    public static ushort EffectiveMaximum(ushort maximum) => maximum == 0 ? (ushort)100 : maximum;
}

/// <summary>What a brightness or volume change did.</summary>
public sealed record ControlOutcome(ResolvedMonitor Monitor, MonitorControl Control, int Percent, DisplayEndpoint Endpoint);
