using System.Text;

namespace MonitorSwitcher.Core.Monitors;

/// <summary>The identifying fields of an EDID base block.</summary>
public sealed record EdidInfo(
    string ManufacturerId,
    ushort ProductCode,
    uint SerialNumber,
    string? MonitorName,
    string? SerialString,
    int ManufactureWeek,
    int ManufactureYear,
    bool ChecksumValid);

/// <summary>Parses the 128-byte EDID base block. Pure function: never touches hardware.</summary>
public static class EdidParser
{
    private static readonly byte[] Header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    public static bool TryParse(ReadOnlySpan<byte> edid, out EdidInfo? info)
    {
        info = null;
        if (edid.Length < 128 || !edid[..8].SequenceEqual(Header))
        {
            return false;
        }

        var manufacturer = DecodeManufacturerId((ushort)((edid[8] << 8) | edid[9]));
        if (manufacturer is null)
        {
            return false;
        }

        ushort product = (ushort)(edid[10] | (edid[11] << 8));
        uint serial = (uint)(edid[12] | (edid[13] << 8) | (edid[14] << 16) | (edid[15] << 24));

        string? name = null;
        string? serialText = null;
        // Four 18-byte descriptors at 54, 72, 90, 108. Display descriptors start with 00 00 00 <tag>.
        for (int offset = 54; offset <= 108; offset += 18)
        {
            if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 2] != 0)
            {
                continue; // detailed timing descriptor
            }

            var text = DecodeDescriptorText(edid.Slice(offset + 5, 13));
            switch (edid[offset + 3])
            {
                case 0xFC: name ??= text; break;
                case 0xFF: serialText ??= text; break;
            }
        }

        byte sum = 0;
        foreach (var b in edid[..128])
        {
            sum += b;
        }

        info = new EdidInfo(
            manufacturer,
            product,
            serial,
            string.IsNullOrEmpty(name) ? null : name,
            string.IsNullOrEmpty(serialText) ? null : serialText,
            edid[16],
            edid[17] + 1990,
            sum == 0);
        return true;
    }

    /// <summary>Decodes the big-endian, 5-bits-per-letter PNP id ("GSM", "DEL", ...).</summary>
    public static string? DecodeManufacturerId(ushort raw)
    {
        Span<char> letters = stackalloc char[3];
        for (int i = 0; i < 3; i++)
        {
            int code = (raw >> (10 - 5 * i)) & 0x1F;
            if (code is < 1 or > 26)
            {
                return null;
            }
            letters[i] = (char)('A' + code - 1);
        }
        return new string(letters);
    }

    private static string DecodeDescriptorText(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0x0A);
        if (end < 0)
        {
            end = bytes.Length;
        }
        return Encoding.ASCII.GetString(bytes[..end]).Trim().TrimEnd('\0');
    }
}
