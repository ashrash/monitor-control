namespace MonitorSwitcher.Core.Ddc;

/// <summary>
/// A DDC/CI "Set VCP Feature" message. The same layout carries the standard MCCS input command
/// (source 0x51, VCP 0x60) and vendor variants such as LG's (source 0x50, VCP 0xF4).
/// </summary>
/// <remarks>
/// On the wire, after the I2C start condition:
/// <code>0x6E | source | 0x84 | 0x03 | vcp | value-hi | value-lo | checksum</code>
/// The checksum is the XOR of every preceding byte, including the 0x6E device address.
/// </remarks>
public readonly record struct SetVcpFrame(byte SourceAddress, byte VcpCode, ushort Value)
{
    /// <summary>Number of bytes that follow the source byte: length, opcode, vcp, hi, lo, checksum.</summary>
    public const int PayloadLength = 6;

    /// <summary>
    /// Bytes that follow the source/register byte. NVIDIA's I2C API takes the source byte as the
    /// "register address" and this array as the data.
    /// </summary>
    public byte[] BuildPayload()
    {
        var payload = new byte[PayloadLength];
        payload[0] = DdcConstants.LengthFlag | 4; // 0x84: four message bytes follow
        payload[1] = DdcConstants.SetVcpOpcode;
        payload[2] = VcpCode;
        payload[3] = (byte)(Value >> 8);
        payload[4] = (byte)(Value & 0xFF);
        payload[5] = ComputeChecksum(SourceAddress, payload.AsSpan(0, PayloadLength - 1));
        return payload;
    }

    /// <summary>
    /// The full message including the 0x6E device address, for APIs (such as AMD ADL) that
    /// take the whole buffer.
    /// </summary>
    public byte[] BuildMessage()
    {
        var payload = BuildPayload();
        var message = new byte[2 + payload.Length];
        message[0] = DdcConstants.DisplayDeviceAddress;
        message[1] = SourceAddress;
        payload.CopyTo(message, 2);
        return message;
    }

    /// <summary>XOR of the 0x6E device address, the source byte and <paramref name="bytes"/>.</summary>
    public static byte ComputeChecksum(byte sourceAddress, ReadOnlySpan<byte> bytes)
    {
        byte checksum = (byte)(DdcConstants.DisplayDeviceAddress ^ sourceAddress);
        foreach (var b in bytes)
        {
            checksum ^= b;
        }
        return checksum;
    }

    public override string ToString() =>
        $"SetVCP(source=0x{SourceAddress:X2}, vcp=0x{VcpCode:X2}, value=0x{Value:X4})";
}
