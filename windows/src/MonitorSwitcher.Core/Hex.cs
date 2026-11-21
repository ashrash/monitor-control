using System.Globalization;

namespace MonitorSwitcher.Core;

/// <summary>Hex helpers for profile files, which store codes as strings like "0xD1".</summary>
public static class Hex
{
    public static bool TryParseUInt16(string? text, out ushort value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }

        return span.Length is > 0 and <= 4
            && ushort.TryParse(span, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    public static ushort ParseUInt16(string text) =>
        TryParseUInt16(text, out var value)
            ? value
            : throw new FormatException($"'{text}' is not a hex value between 0x0 and 0xFFFF.");

    /// <summary>Formats as 0xNN, or 0xNNNN when the value needs more than one byte.</summary>
    public static string Format(ushort value) =>
        value <= 0xFF ? $"0x{value:X2}" : $"0x{value:X4}";

    /// <summary>Parses "84 03 F4" style byte lists.</summary>
    public static byte[] ParseBytes(string text) =>
        text.Split((char[])[' ', ',', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => byte.Parse(t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? t[2..] : t,
                NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture))
            .ToArray();

    public static string FormatBytes(ReadOnlySpan<byte> bytes) =>
        string.Join(' ', bytes.ToArray().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
}
