using System.Text.RegularExpressions;
using MonitorSwitcher.Core.Protocols;

namespace MonitorSwitcher.Core.Profiles;

/// <summary>
/// Checks a profile before it can be used. A profile is the source of truth for which values may
/// be sent to a monitor, so a bad one is rejected rather than half-used.
/// </summary>
public static partial class ProfileValidator
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex ManufacturerPattern();

    public static IReadOnlyList<string> Validate(MonitorProfile profile)
    {
        var errors = new List<string>();

        if (!IdPattern().IsMatch(profile.Id))
        {
            errors.Add($"id '{profile.Id}' must be lowercase letters, digits and dashes (e.g. lg-32un880).");
        }
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            errors.Add("name is required.");
        }

        var match = profile.Match;
        if (!ManufacturerPattern().IsMatch(match.Manufacturer))
        {
            errors.Add($"match.manufacturer '{match.Manufacturer}' must be a three-letter EDID id such as GSM.");
        }

        bool hasProduct = match.ProductCodes is { Count: > 0 };
        bool hasCaps = !string.IsNullOrWhiteSpace(match.CapabilitiesModel);
        bool hasNames = match.ModelNames is { Count: > 0 };
        if (!hasProduct && !hasCaps && !hasNames)
        {
            errors.Add("match needs productCodes, capabilitiesModel or modelNames; a profile may not claim every monitor from a manufacturer.");
        }
        foreach (var code in match.ProductCodes ?? [])
        {
            if (!Hex.TryParseUInt16(code, out _) || !code.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"match.productCodes entry '{code}' must be a hex string like \"0x774F\".");
            }
        }
        if (hasNames && match.ModelNames!.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("match.modelNames may not contain empty names.");
        }

        if (ProtocolRegistry.Find(profile.Protocol) is null)
        {
            errors.Add($"protocol '{profile.Protocol}' is unknown. Known: {string.Join(", ", ProtocolRegistry.All.Select(p => p.Id))}.");
        }

        if (profile.Inputs.Count == 0)
        {
            errors.Add("inputs must list at least one input.");
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in profile.Inputs)
        {
            if (!IdPattern().IsMatch(input.Id))
            {
                errors.Add($"input id '{input.Id}' must be lowercase letters, digits and dashes (e.g. hdmi1, usbc).");
            }
            else if (!seen.Add(input.Id))
            {
                errors.Add($"input id '{input.Id}' appears more than once.");
            }
            if (string.IsNullOrWhiteSpace(input.Name))
            {
                errors.Add($"input '{input.Id}' needs a name.");
            }
            if (!Hex.TryParseUInt16(input.Code, out _) || !input.Code.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"input '{input.Id}' code '{input.Code}' must be a hex string like \"0xD1\".");
            }
        }

        if (profile.Quirks is { } q)
        {
            if (q.Retries is < 0 or > 5)
            {
                errors.Add("quirks.retries must be between 0 and 5.");
            }
            if (q.RetryDelayMs is < 0 or > 5000 || q.PostSwitchDelayMs is < 0 or > 5000)
            {
                errors.Add("quirks delays must be between 0 and 5000 ms.");
            }
        }

        return errors;
    }
}
