using System.Runtime.InteropServices;
using System.Text;

namespace MonitorSwitcher.Windows.Transports.NvApi;

public sealed class NvApiException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Thin binding to nvapi64.dll. NvAPI exports a single function, nvapi_QueryInterface, which
/// returns the address of every other function by a 32-bit id (ids from NVIDIA's open-source
/// nvapi_interface.h). Function pointers keep this Native-AOT friendly.
/// </summary>
internal static unsafe class NvApiNative
{
    public const int Ok = 0;
    public const int NoImplementation = -3;
    public const int NvidiaDeviceNotFound = -6;
    public const int InvalidHandle = -8;
    public const int HandleInvalidated = -10;
    public const int MaxPhysicalGpus = 64;

    public static class Ids
    {
        public const uint Initialize = 0x0150E828;
        public const uint EnumPhysicalGpus = 0xE5AC921F;
        public const uint GetConnectedOutputs = 0x1730BFC9;
        public const uint GetAllOutputs = 0x7D554F8E;
        public const uint GetFullName = 0xCEEE8E9F;
        public const uint GetEdid = 0x37D32E69;
        public const uint I2CWrite = 0xE812EB07;
        public const uint GetDisplayIdByDisplayName = 0xAE457190;
        public const uint GetGpuAndOutputIdFromDisplayId = 0x112BA1A5;
        public const uint GetErrorMessage = 0x6C2D048C;
    }

    /// <summary>NV_I2C_INFO_V3. 64 bytes on x64.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct I2CInfoV3
    {
        public uint Version;
        public uint DisplayMask;
        public byte IsDdcPort;
        public byte DeviceAddress;
        public byte* RegisterAddress;
        public uint RegisterAddressSize;
        public byte* Data;
        public uint DataSize;
        /// <summary>Deprecated; must be 0xFFFF.</summary>
        public uint I2CSpeed;
        /// <summary>NV_I2C_SPEED; 0 = driver default.</summary>
        public int I2CSpeedKhz;
        public byte PortId;
        public uint IsPortIdSet;
    }

    /// <summary>NV_EDID_V3. 272 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EdidV3
    {
        public uint Version;
        public fixed byte Data[256];
        public uint SizeOfEdid;
        public uint EdidId;
        public uint Offset;
    }

    public static uint MakeVersion<T>(int version) where T : unmanaged => (uint)sizeof(T) | ((uint)version << 16);

    private static readonly Lock Gate = new();
    private static delegate* unmanaged[Cdecl]<uint, nint> s_queryInterface;

    /// <summary>Loads nvapi64.dll from System32 only, so a planted DLL next to the app is never used.</summary>
    public static bool TryLoad(out string? error)
    {
        lock (Gate)
        {
            if (s_queryInterface is not null)
            {
                error = null;
                return true;
            }

            if (!NativeLibrary.TryLoad("nvapi64.dll", typeof(NvApiNative).Assembly, DllImportSearchPath.System32, out var library))
            {
                error = "nvapi64.dll was not found, so no NVIDIA driver is installed.";
                return false;
            }

            if (!NativeLibrary.TryGetExport(library, "nvapi_QueryInterface", out var queryInterface))
            {
                error = "nvapi64.dll does not export nvapi_QueryInterface.";
                return false;
            }

            s_queryInterface = (delegate* unmanaged[Cdecl]<uint, nint>)queryInterface;
            error = null;
            return true;
        }
    }

    public static bool Has(uint id) => s_queryInterface is not null && s_queryInterface(id) != 0;

    private static nint Fn(uint id)
    {
        if (s_queryInterface is null)
        {
            throw new NvApiException("nvapi64.dll is not loaded.", NoImplementation);
        }
        var pointer = s_queryInterface(id);
        return pointer != 0
            ? pointer
            : throw new NvApiException($"This NVIDIA driver does not provide NvAPI function 0x{id:X8}.", NoImplementation);
    }

    public static int Initialize() =>
        ((delegate* unmanaged[Cdecl]<int>)Fn(Ids.Initialize))();

    public static int EnumPhysicalGpus(nint* handles, int* count) =>
        ((delegate* unmanaged[Cdecl]<nint*, int*, int>)Fn(Ids.EnumPhysicalGpus))(handles, count);

    public static int GetConnectedOutputs(nint gpu, uint* outputMask) =>
        ((delegate* unmanaged[Cdecl]<nint, uint*, int>)Fn(Ids.GetConnectedOutputs))(gpu, outputMask);

    public static int GetAllOutputs(nint gpu, uint* outputMask) =>
        ((delegate* unmanaged[Cdecl]<nint, uint*, int>)Fn(Ids.GetAllOutputs))(gpu, outputMask);

    public static int GetFullName(nint gpu, byte* name) =>
        ((delegate* unmanaged[Cdecl]<nint, byte*, int>)Fn(Ids.GetFullName))(gpu, name);

    public static int GetEdid(nint gpu, uint outputId, EdidV3* edid) =>
        ((delegate* unmanaged[Cdecl]<nint, uint, EdidV3*, int>)Fn(Ids.GetEdid))(gpu, outputId, edid);

    public static int I2CWrite(nint gpu, I2CInfoV3* info) =>
        ((delegate* unmanaged[Cdecl]<nint, I2CInfoV3*, int>)Fn(Ids.I2CWrite))(gpu, info);

    public static int GetDisplayIdByDisplayName(byte* asciiName, uint* displayId) =>
        ((delegate* unmanaged[Cdecl]<byte*, uint*, int>)Fn(Ids.GetDisplayIdByDisplayName))(asciiName, displayId);

    public static int GetGpuAndOutputIdFromDisplayId(uint displayId, nint* gpu, uint* outputId) =>
        ((delegate* unmanaged[Cdecl]<uint, nint*, uint*, int>)Fn(Ids.GetGpuAndOutputIdFromDisplayId))(displayId, gpu, outputId);

    public static string ErrorMessage(int status)
    {
        if (!Has(Ids.GetErrorMessage))
        {
            return $"NvAPI status {status}";
        }

        var buffer = stackalloc byte[64];
        new Span<byte>(buffer, 64).Clear();
        var result = ((delegate* unmanaged[Cdecl]<int, byte*, int>)Fn(Ids.GetErrorMessage))(status, buffer);
        return result == Ok ? $"{ReadShortString(buffer)} ({status})" : $"NvAPI status {status}";
    }

    public static string ReadShortString(byte* buffer)
    {
        var span = new ReadOnlySpan<byte>(buffer, 64);
        var end = span.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end >= 0 ? span[..end] : span).Trim();
    }
}
