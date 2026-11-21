using System.Globalization;
using System.Text;

namespace MonitorSwitcher.Core.Monitors;

/// <summary>Parsed DDC/CI capabilities string, e.g. <c>(prot(monitor)type(lcd)model(U2720Q)vcp(10 12 60(0F 11 1B)))</c>.</summary>
public sealed record MonitorCapabilities(
    string Raw,
    string? Model,
    string? MccsVersion,
    IReadOnlyDictionary<byte, IReadOnlyList<ushort>> Vcp)
{
    public bool SupportsVcp(byte code) => Vcp.ContainsKey(code);

    /// <summary>Values listed for a VCP code, or an empty list when none are listed.</summary>
    public IReadOnlyList<ushort> ValuesFor(byte code) =>
        Vcp.TryGetValue(code, out var values) ? values : [];
}

/// <summary>
/// Tolerant parser for capabilities strings. Real monitors get the syntax wrong: the LG 32UN880
/// sends <c>type(lcd)UN880cmds(...)</c> with no <c>model(</c> keyword, so bare text glued to a
/// known keyword is treated as the model.
/// </summary>
public static class CapabilitiesParser
{
    private static readonly string[] KnownKeys =
    [
        "prot", "type", "model", "cmds", "vcp", "mccs_ver", "mswhql", "asset_eep", "mpu", "vcpname", "window",
    ];

    public static MonitorCapabilities Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var text = raw.Trim().TrimEnd('\0');
        if (text.StartsWith('(') && FindClosing(text, 0) == text.Length - 1)
        {
            text = text[1..^1];
        }

        var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bare = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]) || text[i] == ')')
            {
                i++;
                continue;
            }

            int start = i;
            while (i < text.Length && text[i] != '(' && text[i] != ')')
            {
                i++;
            }
            var token = text[start..i].Trim();

            if (i >= text.Length || text[i] != '(')
            {
                bare.Append(token);
                continue;
            }

            int close = FindClosing(text, i);
            var value = close < 0 ? text[(i + 1)..] : text[(i + 1)..close];
            i = close < 0 ? text.Length : close + 1;

            var (prefix, key) = SplitKnownKey(token);
            if (prefix.Length > 0)
            {
                bare.Append(prefix);
            }
            groups.TryAdd(key, value.Trim());
        }

        groups.TryGetValue("model", out var model);
        if (string.IsNullOrWhiteSpace(model) && bare.Length > 0)
        {
            model = bare.ToString();
        }
        groups.TryGetValue("mccs_ver", out var mccs);

        var vcp = groups.TryGetValue("vcp", out var vcpText)
            ? ParseVcp(vcpText)
            : new Dictionary<byte, IReadOnlyList<ushort>>();

        return new MonitorCapabilities(raw, string.IsNullOrWhiteSpace(model) ? null : model.Trim(), mccs?.Trim(), vcp);
    }

    private static (string Prefix, string Key) SplitKnownKey(string token)
    {
        foreach (var key in KnownKeys.OrderByDescending(k => k.Length))
        {
            if (token.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return ("", key);
            }
            if (token.EndsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                return (token[..^key.Length], key);
            }
        }
        return ("", token);
    }

    private static Dictionary<byte, IReadOnlyList<ushort>> ParseVcp(string text)
    {
        var result = new Dictionary<byte, IReadOnlyList<ushort>>();
        int i = 0;
        byte? last = null;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '(')
            {
                int close = FindClosing(text, i);
                var inner = close < 0 ? text[(i + 1)..] : text[(i + 1)..close];
                i = close < 0 ? text.Length : close + 1;
                if (last is byte code)
                {
                    result[code] = inner
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(t => ushort.TryParse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v) ? (ushort?)v : null)
                        .Where(v => v.HasValue)
                        .Select(v => v!.Value)
                        .ToArray();
                }
                continue;
            }

            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '(')
            {
                i++;
            }
            if (byte.TryParse(text.AsSpan(start, i - start), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var vcpCode))
            {
                last = vcpCode;
                result.TryAdd(vcpCode, []);
            }
            else
            {
                last = null;
            }
        }
        return result;
    }

    private static int FindClosing(string text, int openIndex)
    {
        int depth = 0;
        for (int i = openIndex; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')' && --depth == 0)
            {
                return i;
            }
        }
        return -1;
    }
}
