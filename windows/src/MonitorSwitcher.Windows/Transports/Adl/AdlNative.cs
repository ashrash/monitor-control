using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MonitorSwitcher.Windows.Transports.Adl;

/// <summary>
/// Binding to the AMD Display Library (atiadlxx.dll, installed with the AMD driver). Struct
/// layouts follow adl_structures.h from GPUOpen-LibrariesAndSDKs/display-library and are pinned
/// by InteropLayoutTests.
/// </summary>
internal static unsafe class AdlNative
{
    public const int Ok = 0;
    public const int DisplayConnected = 0x1;

    /// <summary>AdapterInfo (Windows layout). 1572 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AdapterInfo
    {
        public int Size;
        public int AdapterIndex;
        public fixed byte Udid[256];
        public int BusNumber;
        public int DeviceNumber;
        public int FunctionNumber;
        public int VendorId;
        public fixed byte AdapterName[256];
        public fixed byte DisplayName[256];
        public int Present;
        public int Exist;
        public fixed byte DriverPath[256];
        public fixed byte DriverPathExt[256];
        public fixed byte PnpString[256];
        public int OsDisplayIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayId
    {
        public int LogicalIndex;
        public int PhysicalIndex;
        public int LogicalAdapterIndex;
        public int PhysicalAdapterIndex;
    }

    /// <summary>ADLDisplayInfo. 552 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayInfo
    {
        public DisplayId Id;
        public int ControllerIndex;
        public fixed byte Name[256];
        public fixed byte ManufacturerName[256];
        public int Type;
        public int OutputType;
        public int Connector;
        public int InfoMask;
        public int InfoValue;
    }

    /// <summary>ADLDisplayEDIDData. 288 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EdidData
    {
        public int Size;
        public int Flag;
        public int EdidSize;
        public int BlockIndex;
        public fixed byte Data[256];
        public fixed int Reserved[4];
    }

    private static readonly Lock Gate = new();
    private static nint s_library;

    public static delegate* unmanaged[Cdecl]<delegate* unmanaged[Stdcall]<int, nint>, int, nint*, int> MainControlCreate;
    public static delegate* unmanaged[Cdecl]<nint, int> MainControlDestroy;
    public static delegate* unmanaged[Cdecl]<nint, int*, int> NumberOfAdaptersGet;
    public static delegate* unmanaged[Cdecl]<nint, AdapterInfo*, int, int> AdapterInfoGet;
    public static delegate* unmanaged[Cdecl]<nint, int, int*, DisplayInfo**, int, int> DisplayInfoGet;
    public static delegate* unmanaged[Cdecl]<nint, int, int, EdidData*, int> EdidDataGet;
    public static delegate* unmanaged[Cdecl]<nint, int, int, int, int, int, byte*, int*, byte*, int> DdcBlockAccessGet;

    /// <summary>Loads atiadlxx.dll from System32 only.</summary>
    public static bool TryLoad(out string? error)
    {
        lock (Gate)
        {
            if (s_library != 0)
            {
                error = null;
                return true;
            }

            if (!NativeLibrary.TryLoad("atiadlxx.dll", typeof(AdlNative).Assembly, DllImportSearchPath.System32, out var library))
            {
                error = "atiadlxx.dll was not found, so no AMD driver is installed.";
                return false;
            }

            try
            {
                MainControlCreate = (delegate* unmanaged[Cdecl]<delegate* unmanaged[Stdcall]<int, nint>, int, nint*, int>)Export(library, "ADL2_Main_Control_Create");
                MainControlDestroy = (delegate* unmanaged[Cdecl]<nint, int>)Export(library, "ADL2_Main_Control_Destroy");
                NumberOfAdaptersGet = (delegate* unmanaged[Cdecl]<nint, int*, int>)Export(library, "ADL2_Adapter_NumberOfAdapters_Get");
                AdapterInfoGet = (delegate* unmanaged[Cdecl]<nint, AdapterInfo*, int, int>)Export(library, "ADL2_Adapter_AdapterInfo_Get");
                DisplayInfoGet = (delegate* unmanaged[Cdecl]<nint, int, int*, DisplayInfo**, int, int>)Export(library, "ADL2_Display_DisplayInfo_Get");
                EdidDataGet = (delegate* unmanaged[Cdecl]<nint, int, int, EdidData*, int>)Export(library, "ADL2_Display_EdidData_Get");
                DdcBlockAccessGet = (delegate* unmanaged[Cdecl]<nint, int, int, int, int, int, byte*, int*, byte*, int>)Export(library, "ADL2_Display_DDCBlockAccess_Get");
            }
            catch (EntryPointNotFoundException ex)
            {
                NativeLibrary.Free(library);
                error = $"This AMD driver's ADL is missing {ex.Message}.";
                return false;
            }

            s_library = library;
            error = null;
            return true;
        }
    }

    private static nint Export(nint library, string name) =>
        NativeLibrary.TryGetExport(library, name, out var address) ? address : throw new EntryPointNotFoundException(name);

    /// <summary>ADL allocates result arrays through this callback; the caller frees them with <see cref="Free"/>.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    public static nint Alloc(int size) => Marshal.AllocHGlobal(size);

    public static void Free(void* pointer)
    {
        if (pointer is not null)
        {
            Marshal.FreeHGlobal((nint)pointer);
        }
    }

    public static string ReadString(byte* text, int capacity)
    {
        var span = new ReadOnlySpan<byte>(text, capacity);
        var end = span.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end >= 0 ? span[..end] : span).Trim();
    }
}
