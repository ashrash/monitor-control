namespace MonitorSwitcher.Core.Ddc;

/// <summary>Fixed values from the VESA DDC/CI and MCCS standards.</summary>
public static class DdcConstants
{
    /// <summary>
    /// 8-bit I2C write address of a display's DDC/CI interface. This is the ONLY device address
    /// Monitor Switcher ever writes to. Transports never take a device address from callers.
    /// </summary>
    public const byte DisplayDeviceAddress = 0x6E;

    /// <summary>7-bit form of <see cref="DisplayDeviceAddress"/> (APIs such as macOS IOAVService use it).</summary>
    public const byte DisplayDeviceAddress7Bit = 0x37;

    /// <summary>8-bit address of the EDID EEPROM. Documented so the guard can name it; never written.</summary>
    public const byte EdidDeviceAddress = 0xA0;

    /// <summary>Host source address used by every standard MCCS command.</summary>
    public const byte StandardSourceAddress = 0x51;

    /// <summary>LG's alternative source address, used for its vendor input-select command.</summary>
    public const byte LgAlternativeSourceAddress = 0x50;

    /// <summary>"Set VCP Feature" opcode.</summary>
    public const byte SetVcpOpcode = 0x03;

    /// <summary>Length byte = 0x80 | number of message bytes that follow (excluding the checksum).</summary>
    public const byte LengthFlag = 0x80;

    /// <summary>Standard MCCS "Input Select" VCP code.</summary>
    public const byte InputSelectVcp = 0x60;
}
