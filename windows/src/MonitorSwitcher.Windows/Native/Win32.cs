using System.Runtime.InteropServices;

namespace MonitorSwitcher.Windows.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct Luid
{
    public uint LowPart;
    public int HighPart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Rect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

/// <summary>QueryDisplayConfig and friends (user32). Layouts follow wingdi.h.</summary>
internal static unsafe partial class DisplayConfig
{
    public const uint QdcOnlyActivePaths = 0x2;
    public const int ErrorSuccess = 0;
    public const int ErrorInsufficientBuffer = 122;
    public const uint DeviceInfoGetSourceName = 1;
    public const uint DeviceInfoGetTargetName = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct PathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public Rational RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PathInfo
    {
        public PathSourceInfo SourceInfo;
        public PathTargetInfo TargetInfo;
        public uint Flags;
    }

    /// <summary>Only the header fields are read; the union is padded to the native 64-byte size.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    public struct ModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SourceDeviceName
    {
        public DeviceInfoHeader Header;
        public fixed char ViewGdiDeviceName[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TargetDeviceName
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        public fixed char MonitorFriendlyDeviceName[64];
        public fixed char MonitorDevicePath[128];
    }

    [LibraryImport("user32.dll")]
    public static partial int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [LibraryImport("user32.dll")]
    public static partial int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        PathInfo* pathArray,
        ref uint numModeInfoArrayElements,
        ModeInfo* modeInfoArray,
        nint currentTopologyId);

    [LibraryImport("user32.dll")]
    public static partial int DisplayConfigGetDeviceInfo(DeviceInfoHeader* requestPacket);
}

internal static unsafe partial class User32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfoEx
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        public fixed char Device[32];
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(nint hdc, nint clip, delegate* unmanaged<nint, nint, nint, nint, int> callback, nint data);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint monitor, MonitorInfoEx* info);

    /// <summary>All HMONITORs, in the order Windows reports them.</summary>
    public static List<nint> EnumerateMonitors()
    {
        var list = new List<nint>();
        var handle = GCHandle.Alloc(list);
        try
        {
            EnumDisplayMonitors(0, 0, &MonitorEnumProc, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        return list;
    }

    public static string? GetDeviceName(nint monitor)
    {
        var info = new MonitorInfoEx { Size = (uint)sizeof(MonitorInfoEx) };
        return GetMonitorInfo(monitor, &info) ? new string(info.Device) : null;
    }

    [UnmanagedCallersOnly]
    private static int MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data)
    {
        ((List<nint>)GCHandle.FromIntPtr(data).Target!).Add(monitor);
        return 1;
    }
}

/// <summary>Monitor Configuration API (dxva2.dll). Standard MCCS only: it always uses source 0x51.</summary>
internal static unsafe partial class Dxva2
{
    [StructLayout(LayoutKind.Sequential)]
    public struct PhysicalMonitor
    {
        public nint Handle;
        public fixed char Description[128];
    }

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint monitor, out uint count);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetPhysicalMonitorsFromHMONITOR(nint monitor, uint count, PhysicalMonitor* monitors);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyPhysicalMonitors(uint count, PhysicalMonitor* monitors);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetVCPFeature(nint monitor, byte vcpCode, uint newValue);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVCPFeatureAndVCPFeatureReply(nint monitor, byte vcpCode, out uint codeType, out uint currentValue, out uint maximumValue);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCapabilitiesStringLength(nint monitor, out uint length);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CapabilitiesRequestAndCapabilitiesReply(nint monitor, byte* buffer, uint length);
}
