using MonitorSwitcher.Windows.Native;
using MonitorSwitcher.Windows.Transports.Adl;
using MonitorSwitcher.Windows.Transports.Igcl;
using MonitorSwitcher.Windows.Transports.NvApi;

namespace MonitorSwitcher.Tests;

/// <summary>
/// A wrong struct size silently corrupts driver calls, so pin every native layout to the size of
/// its C counterpart (x64).
/// </summary>
public unsafe class InteropLayoutTests
{
    [Fact]
    public void Nvapi_structs_match_nvapi_h()
    {
        Assert.Equal(64, sizeof(NvApiNative.I2CInfoV3));
        Assert.Equal(272, sizeof(NvApiNative.EdidV3));
        Assert.Equal(64u | (3u << 16), NvApiNative.MakeVersion<NvApiNative.I2CInfoV3>(3));
    }

    [Fact]
    public void Display_config_structs_match_wingdi_h()
    {
        Assert.Equal(72, sizeof(DisplayConfig.PathInfo));
        Assert.Equal(64, sizeof(DisplayConfig.ModeInfo));
        Assert.Equal(20, sizeof(DisplayConfig.DeviceInfoHeader));
        Assert.Equal(84, sizeof(DisplayConfig.SourceDeviceName));
        Assert.Equal(420, sizeof(DisplayConfig.TargetDeviceName));
    }

    [Fact]
    public void Monitor_api_structs_match_the_windows_headers()
    {
        Assert.Equal(104, sizeof(User32.MonitorInfoEx));
        Assert.Equal(264, sizeof(Dxva2.PhysicalMonitor));
    }

    [Fact]
    public void Adl_structs_match_adl_structures_h()
    {
        Assert.Equal((9 * 4) + (6 * 256), sizeof(AdlNative.AdapterInfo)); // 9 ints + 6 ADL_MAX_PATH strings = 1572
        Assert.Equal(16, sizeof(AdlNative.DisplayId));
        Assert.Equal(552, sizeof(AdlNative.DisplayInfo));
        Assert.Equal(288, sizeof(AdlNative.EdidData));
    }

    [Fact]
    public void Igcl_structs_match_igcl_api_h()
    {
        Assert.Equal(36, sizeof(IgclNative.InitArgs));
        Assert.Equal(168, sizeof(IgclNative.I2CAccessArgs));
        Assert.Equal(64, sizeof(IgclNative.DisplayTiming));
        Assert.Equal(200, sizeof(IgclNative.DisplayProperties));
    }

    [Fact]
    public void Igcl_i2c_fields_sit_where_the_driver_reads_them()
    {
        // If Address or Offset were shifted, the driver could address the wrong I2C device.
        var args = default(IgclNative.I2CAccessArgs);
        byte* start = (byte*)&args;
        Assert.Equal(8, (byte*)&args.DataSize - start);
        Assert.Equal(12, (byte*)&args.Address - start);
        Assert.Equal(16, (byte*)&args.OpType - start);
        Assert.Equal(20, (byte*)&args.Offset - start);
        Assert.Equal(24, (byte*)&args.Flags - start);
        Assert.Equal(32, (byte*)&args.Rad - start);
        Assert.Equal(40, args.Data - start);
    }

    [Fact]
    public void Nvapi_i2c_fields_sit_where_the_driver_reads_them()
    {
        var info = default(NvApiNative.I2CInfoV3);
        byte* start = (byte*)&info;
        Assert.Equal(8, &info.IsDdcPort - start);
        Assert.Equal(9, &info.DeviceAddress - start);
        Assert.Equal(16, (byte*)&info.RegisterAddress - start);
        Assert.Equal(32, (byte*)&info.Data - start);
        Assert.Equal(48, (byte*)&info.I2CSpeedKhz - start);
    }

    [Fact]
    public void Igcl_display_properties_flags_are_at_the_header_offset()
    {
        var props = default(IgclNative.DisplayProperties);
        byte* start = (byte*)&props;
        Assert.Equal(8, (byte*)&props.OsDisplayEncoderHandle - start);
        Assert.Equal(48, (byte*)&props.DisplayConfigFlags - start);
        Assert.Equal(72, (byte*)&props.DisplayTimingInfo - start);
    }
}
