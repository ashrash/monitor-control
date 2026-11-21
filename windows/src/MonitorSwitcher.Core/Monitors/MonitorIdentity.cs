using System.Text.Json.Serialization;

namespace MonitorSwitcher.Core.Monitors;

/// <summary>
/// Who a monitor is, independent of which port or GPU it is plugged into. Settings and hotkeys
/// are keyed by <see cref="StableId"/> so they survive cable moves and display renumbering.
/// </summary>
public sealed record MonitorIdentity(
    string ManufacturerId,
    ushort ProductCode,
    uint SerialNumber = 0,
    string? ModelName = null,
    string? SerialString = null)
{
    /// <summary>e.g. "GSM-774F-0005DE50". Falls back to the EDID serial string, then to maker+product.</summary>
    [JsonIgnore]
    public string StableId
    {
        get
        {
            var basePart = $"{ManufacturerId}-{ProductCode:X4}";
            if (SerialNumber != 0)
            {
                return $"{basePart}-{SerialNumber:X8}";
            }
            if (!string.IsNullOrWhiteSpace(SerialString))
            {
                var clean = new string(SerialString.Where(char.IsLetterOrDigit).ToArray());
                if (clean.Length > 0)
                {
                    return $"{basePart}-{clean}";
                }
            }
            return basePart;
        }
    }

    public static MonitorIdentity FromEdid(EdidInfo edid) =>
        new(edid.ManufacturerId, edid.ProductCode, edid.SerialNumber, edid.MonitorName, edid.SerialString);

    public override string ToString() =>
        $"{ModelName ?? "Unknown monitor"} ({ManufacturerId} 0x{ProductCode:X4})";
}
