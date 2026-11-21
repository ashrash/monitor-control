using System.Text.Json.Serialization;

namespace MonitorSwitcher.Core.Profiles;

public enum ProfileOrigin
{
    /// <summary>Shipped with the app (profiles/ folder in the repo).</summary>
    BuiltIn,
    /// <summary>Created by the setup wizard or by hand in the user's profiles folder.</summary>
    User,
    /// <summary>Generated on the fly for a monitor with no profile (standard MCCS).</summary>
    Fallback,
}

/// <summary>
/// Layer 3: one monitor model, its protocol, and its input codes. Stored as JSON and shared
/// verbatim between the Windows and macOS apps. Schema: profiles/profile.schema.json.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MonitorProfile
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public ProfileMatch Match { get; set; } = new();

    public string Protocol { get; set; } = "";

    public List<ProfileInput> Inputs { get; set; } = [];

    public ProfileQuirks? Quirks { get; set; }

    public string? Notes { get; set; }

    [JsonIgnore]
    public ProfileOrigin Origin { get; set; }

    /// <summary>File the profile came from (resource name for built-ins).</summary>
    [JsonIgnore]
    public string? SourcePath { get; set; }

    [JsonIgnore]
    public ProfileQuirks EffectiveQuirks => Quirks ?? ProfileQuirks.Default;

    public ProfileInput? FindInput(string inputId) =>
        Inputs.FirstOrDefault(i => string.Equals(i.Id, inputId, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => $"{Name} [{Id}, {Origin}]";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProfileMatch
{
    /// <summary>Three-letter EDID PNP id, e.g. "GSM" for LG.</summary>
    public string Manufacturer { get; set; } = "";

    /// <summary>EDID product codes as hex strings ("0x774F").</summary>
    public List<string>? ProductCodes { get; set; }

    /// <summary>Model token from the DDC/CI capabilities string ("UN880").</summary>
    public string? CapabilitiesModel { get; set; }

    /// <summary>EDID monitor names. Weakest key: many LG models are all called "LG HDR 4K".</summary>
    public List<string>? ModelNames { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProfileInput
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Hex string, e.g. "0xD1".</summary>
    public string Code { get; set; } = "";

    public bool Verified { get; set; }

    [JsonIgnore]
    public ushort CodeValue => Hex.ParseUInt16(Code);

    public override string ToString() => $"{Name} ({Id}, {Code})";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProfileQuirks
{
    public static readonly ProfileQuirks Default = new();

    /// <summary>Extra attempts when the transport reports a failure.</summary>
    public int Retries { get; set; } = 1;

    public int RetryDelayMs { get; set; } = 100;

    public int PostSwitchDelayMs { get; set; }

    /// <summary>
    /// The monitor ignores DDC/CI that arrives on an input it isn't showing (e.g. LG 32UN880). A
    /// computer can then switch the monitor away from itself, but not back; the other computer,
    /// the monitor's buttons, or the monitor's no-signal fallback has to bring it back.
    /// </summary>
    public bool IgnoresCommandsWhenInactive { get; set; }
}
