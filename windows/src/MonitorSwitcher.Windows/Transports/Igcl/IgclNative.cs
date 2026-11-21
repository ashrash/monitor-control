using System.Runtime.InteropServices;

namespace MonitorSwitcher.Windows.Transports.Igcl;

/// <summary>
/// Binding to the Intel Graphics Control Library (ControlLib.dll, installed with the Intel
/// driver). Layouts follow igcl_api.h from intel/drivers.gpu.control-library and are pinned,
/// field offsets included, by InteropLayoutTests: a shifted Address field could make the driver
/// write to the wrong I2C device.
/// </summary>
internal static unsafe class IgclNative
{
    public const int Success = 0;
    public const int OperationRead = 1;
    public const int OperationWrite = 2;
    public const int MaxI2CData = 0x80;
    public const uint DisplayActive = 1u << 0;
    public const uint DisplayAttached = 1u << 1;

    public static uint MakeVersion(uint major, uint minor) => (major << 16) | (minor & 0xFFFF);

    [StructLayout(LayoutKind.Sequential)]
    public struct ApplicationId
    {
        public uint Data1;
        public ushort Data2;
        public ushort Data3;
        public fixed byte Data4[8];
    }

    /// <summary>ctl_init_args_t. 36 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct InitArgs
    {
        public uint Size;
        public byte Version;
        public uint AppVersion;
        public uint Flags;
        public uint SupportedVersion;
        public ApplicationId ApplicationUid;
    }

    /// <summary>ctl_i2c_access_args_t. 168 bytes; Address at 12, Offset at 20, Data at 40.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct I2CAccessArgs
    {
        public uint Size;
        public byte Version;
        public uint DataSize;
        /// <summary>8-bit device address, 0x6E for DDC/CI (as in Intel's I2C sample).</summary>
        public uint Address;
        public int OpType;
        /// <summary>Register byte: the DDC/CI source address (0x51, or 0x50 for LG).</summary>
        public uint Offset;
        public uint Flags;
        public ulong Rad;
        public fixed byte Data[MaxI2CData];
    }

    /// <summary>ctl_os_display_encoder_identifier_t: a union of a uint and { void*, uint }. 16 bytes.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public struct OsDisplayEncoderId
    {
        [FieldOffset(0)]
        public uint WindowsDisplayEncoderId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Revision
    {
        public byte Major;
        public byte Minor;
        public byte Patch;
    }

    /// <summary>ctl_display_timing_t. 64 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayTiming
    {
        public uint Size;
        public byte Version;
        public ulong PixelClock;
        public uint HActive;
        public uint VActive;
        public uint HTotal;
        public uint VTotal;
        public uint HBlank;
        public uint VBlank;
        public uint HSync;
        public uint VSync;
        public float RefreshRate;
        public int SignalStandard;
        public byte VicId;
    }

    /// <summary>ctl_display_properties_t. 200 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayProperties
    {
        public uint Size;
        public byte Version;
        public OsDisplayEncoderId OsDisplayEncoderHandle;
        public int Type;
        public int AttachedDisplayMuxType;
        public int ProtocolConverterOutput;
        public Revision SupportedSpec;
        public uint SupportedOutputBpcFlags;
        public uint ProtocolConverterType;
        public uint DisplayConfigFlags;
        public uint FeatureEnabledFlags;
        public uint FeatureSupportedFlags;
        public uint AdvancedFeatureEnabledFlags;
        public uint AdvancedFeatureSupportedFlags;
        public DisplayTiming DisplayTimingInfo;
        public fixed uint ReservedFields[16];
    }

    private static readonly Lock Gate = new();
    private static nint s_library;

    public static delegate* unmanaged[Cdecl]<InitArgs*, nint*, int> Init;
    public static delegate* unmanaged[Cdecl]<nint, int> Close;
    public static delegate* unmanaged[Cdecl]<nint, uint*, nint*, int> EnumerateDevices;
    public static delegate* unmanaged[Cdecl]<nint, uint*, nint*, int> EnumerateDisplayOutputs;
    public static delegate* unmanaged[Cdecl]<nint, DisplayProperties*, int> GetDisplayProperties;
    public static delegate* unmanaged[Cdecl]<nint, I2CAccessArgs*, int> I2CAccess;

    /// <summary>Loads ControlLib.dll from System32 only.</summary>
    public static bool TryLoad(out string? error)
    {
        lock (Gate)
        {
            if (s_library != 0)
            {
                error = null;
                return true;
            }

            if (!NativeLibrary.TryLoad("ControlLib.dll", typeof(IgclNative).Assembly, DllImportSearchPath.System32, out var library))
            {
                error = "ControlLib.dll was not found, so no recent Intel graphics driver is installed.";
                return false;
            }

            try
            {
                Init = (delegate* unmanaged[Cdecl]<InitArgs*, nint*, int>)Export(library, "ctlInit");
                Close = (delegate* unmanaged[Cdecl]<nint, int>)Export(library, "ctlClose");
                EnumerateDevices = (delegate* unmanaged[Cdecl]<nint, uint*, nint*, int>)Export(library, "ctlEnumerateDevices");
                EnumerateDisplayOutputs = (delegate* unmanaged[Cdecl]<nint, uint*, nint*, int>)Export(library, "ctlEnumerateDisplayOutputs");
                GetDisplayProperties = (delegate* unmanaged[Cdecl]<nint, DisplayProperties*, int>)Export(library, "ctlGetDisplayProperties");
                I2CAccess = (delegate* unmanaged[Cdecl]<nint, I2CAccessArgs*, int>)Export(library, "ctlI2CAccess");
            }
            catch (EntryPointNotFoundException ex)
            {
                NativeLibrary.Free(library);
                error = $"This Intel driver's ControlLib.dll is missing {ex.Message}.";
                return false;
            }

            s_library = library;
            error = null;
            return true;
        }
    }

    private static nint Export(nint library, string name) =>
        NativeLibrary.TryGetExport(library, name, out var address) ? address : throw new EntryPointNotFoundException(name);
}
