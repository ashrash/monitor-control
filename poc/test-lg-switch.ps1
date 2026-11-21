# test-lg-switch.ps1
# Quick test: sends LG's input-switch command through the NVIDIA driver.
#
# Usage (from PowerShell):
#   powershell -ExecutionPolicy Bypass -File test-lg-switch.ps1 -Code D1
#
# LG codes to try:  D1 = USB-C,  D0 = DisplayPort,  90 = HDMI 1,  91 = HDMI 2
# If D1 does nothing, also try D2 and D3.

param([string]$Code = "D1")

$value = [Convert]::ToByte($Code, 16)

Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class NvI2C {
    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumGpusFn([Out] IntPtr[] h, out int n);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int OutputsFn(IntPtr gpu, out uint mask);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int WriteFn(IntPtr gpu, ref I2CInfo info);

    [StructLayout(LayoutKind.Sequential)]
    struct I2CInfo {
        public uint version; public uint displayMask;
        public byte bIsDDCPort; public byte i2cDevAddress;
        public IntPtr pbI2cRegAddress; public uint regAddrSize;
        public IntPtr pbData; public uint cbSize;
        public uint i2cSpeed; public int i2cSpeedKhz;
        public byte portId; public uint bIsPortIdSet;
    }

    static T Get<T>(uint id) where T : class {
        IntPtr p = QueryInterface(id);
        if (p == IntPtr.Zero) throw new Exception("NVIDIA function missing: 0x" + id.ToString("X8"));
        return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
    }

    public static string Send(byte vcp, byte value, byte source) {
        if (Get<InitFn>(0x0150E828)() != 0) return "NVIDIA driver initialization failed.";
        EnumGpusFn enumGpus = Get<EnumGpusFn>(0xE5AC921F);
        OutputsFn outputs = Get<OutputsFn>(0x1730BFC9);
        WriteFn write = Get<WriteFn>(0xE812EB07);

        IntPtr[] gpus = new IntPtr[64]; int n;
        if (enumGpus(gpus, out n) != 0) return "No NVIDIA GPU found.";

        string log = "";
        for (int g = 0; g < n; g++) {
            uint mask;
            if (outputs(gpus[g], out mask) != 0) continue;
            for (int bit = 0; bit < 32; bit++) {
                uint output = 1u << bit;
                if ((mask & output) == 0) continue;

                byte chk = (byte)(0x6E ^ source ^ 0x84 ^ 0x03 ^ vcp ^ 0x00 ^ value);
                byte[] data = { 0x84, 0x03, vcp, 0x00, value, chk };
                IntPtr pSrc = Marshal.AllocHGlobal(1), pData = Marshal.AllocHGlobal(data.Length);
                try {
                    Marshal.WriteByte(pSrc, source);
                    Marshal.Copy(data, 0, pData, data.Length);
                    I2CInfo i = new I2CInfo();
                    i.version = (uint)Marshal.SizeOf(typeof(I2CInfo)) | (3u << 16);
                    i.displayMask = output; i.bIsDDCPort = 1; i.i2cDevAddress = 0x6E;
                    i.pbI2cRegAddress = pSrc; i.regAddrSize = 1;
                    i.pbData = pData; i.cbSize = (uint)data.Length;
                    i.i2cSpeed = 0xFFFF; i.i2cSpeedKhz = 0;
                    int r = write(gpus[g], ref i);
                    log += "GPU " + g + ", output 0x" + output.ToString("X") + ": " + (r == 0 ? "sent OK" : "failed (" + r + ")") + "\n";
                } finally { Marshal.FreeHGlobal(pSrc); Marshal.FreeHGlobal(pData); }
            }
        }
        return log == "" ? "No connected displays found." : log;
    }
}
"@

Write-Host ("Sending LG input code 0x{0:X2}..." -f $value)
Write-Host ([NvI2C]::Send(0xF4, $value, 0x50))
