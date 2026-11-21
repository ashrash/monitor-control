using MonitorSwitcher.Core.Controls;

namespace MonitorSwitcher.Core.Ddc;

/// <summary>
/// The last line of defence before bytes reach a monitor. Every transport runs these checks
/// immediately before its native write call.
/// </summary>
/// <remarks>
/// Rules:
/// <list type="bullet">
/// <item>The I2C device address must be 0x6E (DDC/CI). 0xA0 is the EDID EEPROM; writing to it can brick a monitor.</item>
/// <item>The source/register byte must be 0x51 (standard) or 0x50 (LG alternative). Note that 0x50 is only
/// allowed in this position, inside a DDC/CI message to 0x6E, never as a device address.</item>
/// <item>Full messages must carry a correct checksum.</item>
/// </list>
/// Values are validated earlier, against the monitor's profile, by <c>SwitchService</c>.
/// </remarks>
public static class DdcGuard
{
    private static readonly byte[] AllowedSources =
    [
        DdcConstants.StandardSourceAddress,
        DdcConstants.LgAlternativeSourceAddress,
    ];

    public static IReadOnlyList<byte> AllowedSourceAddresses => AllowedSources;

    public static void EnsureDeviceAddress(byte deviceAddress)
    {
        if (deviceAddress != DdcConstants.DisplayDeviceAddress)
        {
            throw new DdcSafetyException(
                $"Refused to write to I2C device address 0x{deviceAddress:X2}. Only the DDC/CI address 0x6E is ever written.");
        }
    }

    public static void EnsureFrame(in SetVcpFrame frame)
    {
        if (Array.IndexOf(AllowedSources, frame.SourceAddress) < 0)
        {
            throw new DdcSafetyException(
                $"Refused to send {frame}: source byte 0x{frame.SourceAddress:X2} is not one of 0x51 or 0x50.");
        }
    }

    /// <summary>VCP codes the brightness/volume path may write. Input switching has its own path.</summary>
    public static readonly IReadOnlyList<byte> ControlVcpCodes = [.. MonitorControls.All.Select(c => c.VcpCode())];

    /// <summary>Brightness and volume writes: standard source byte and one of <see cref="ControlVcpCodes"/> only.</summary>
    public static void EnsureControlFrame(in SetVcpFrame frame)
    {
        EnsureFrame(frame);
        if (frame.SourceAddress != DdcConstants.StandardSourceAddress || !ControlVcpCodes.Contains(frame.VcpCode))
        {
            throw new DdcSafetyException($"Refused to send {frame}: only brightness (0x10) and volume (0x62) may be set this way.");
        }
    }

    /// <summary>Checks a complete message (device address first, checksum last).</summary>
    public static void EnsureMessage(ReadOnlySpan<byte> message)
    {
        if (message.Length < 3)
        {
            throw new DdcSafetyException("Refused to send a DDC/CI message shorter than 3 bytes.");
        }

        EnsureDeviceAddress(message[0]);

        if (Array.IndexOf(AllowedSources, message[1]) < 0)
        {
            throw new DdcSafetyException($"Refused to send a DDC/CI message with source byte 0x{message[1]:X2}.");
        }

        byte xor = 0;
        foreach (var b in message[..^1])
        {
            xor ^= b;
        }
        if (xor != message[^1])
        {
            throw new DdcSafetyException(
                $"Refused to send a DDC/CI message with checksum 0x{message[^1]:X2}; expected 0x{xor:X2}.");
        }
    }
}
