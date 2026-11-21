using System.Runtime.InteropServices;
using System.Text;
using MonitorSwitcher.Core.Ddc;
using MonitorSwitcher.Core.Logging;
using MonitorSwitcher.Core.Transports;
using MonitorSwitcher.Windows.Displays;
using MonitorSwitcher.Windows.Native;

namespace MonitorSwitcher.Windows.Transports.Dxva2Api;

/// <summary>
/// Windows' built-in Monitor Configuration API (dxva2.dll). Works with any GPU, but it builds the
/// DDC/CI frame itself with source 0x51, so it can only send standard MCCS commands. It is also
/// used for reads (capabilities string, VCP values) that the vendor transports don't expose.
/// </summary>
public sealed unsafe class Dxva2Transport(WindowsDisplayEnumerator displays, ILog log) : DdcTransport
{
    private sealed record Target(string GdiDeviceName, int PhysicalIndex);

    public override string Id => "dxva2";

    public override string DisplayName => "Windows DDC/CI (dxva2)";

    public override bool SupportsRawI2C => false;

    public override int Priority => 50;

    public override bool SupportsReads => true;

    public override TransportStatus Probe() =>
        NativeLibrary.TryLoad("dxva2.dll", typeof(Dxva2Transport).Assembly, DllImportSearchPath.System32, out _)
            ? TransportStatus.Available("Built into Windows; standard MCCS commands only.")
            : TransportStatus.Unavailable("dxva2.dll is missing from this Windows installation.");

    public override IReadOnlyList<DisplayEndpoint> Enumerate()
    {
        var byGdiName = displays.GetActiveDisplays()
            .GroupBy(d => d.GdiDeviceName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var result = new List<DisplayEndpoint>();
        foreach (var monitor in User32.EnumerateMonitors())
        {
            var gdiName = User32.GetDeviceName(monitor);
            if (gdiName is null || !Dxva2.GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) || count == 0)
            {
                continue;
            }

            byGdiName.TryGetValue(gdiName, out var targets);
            for (int i = 0; i < count; i++)
            {
                // In clone mode one HMONITOR has several physical monitors; Windows lists them
                // in the same order as the display paths.
                var identity = targets is not null && i < targets.Count ? targets[i].Identity : null;
                if (identity is null)
                {
                    log.Debug($"dxva2: no EDID identity for {gdiName} #{i}; skipped.");
                    continue;
                }

                result.Add(new DisplayEndpoint(this, identity, $"{gdiName} #{i}", new Target(gdiName, i))
                {
                    OsDisplayName = gdiName,
                });
            }
        }
        return result;
    }

    protected override void SendCore(DisplayEndpoint endpoint, SetVcpFrame frame)
    {
        // dxva2 always addresses 0x6E with source 0x51 itself. DdcTransport.Send has already
        // rejected any frame with a different source byte.
        DdcGuard.EnsureDeviceAddress(DdcConstants.DisplayDeviceAddress);
        WithPhysicalMonitor(endpoint, handle =>
        {
            if (!Dxva2.SetVCPFeature(handle, frame.VcpCode, frame.Value))
            {
                int error = Marshal.GetLastPInvokeError();
                throw new DdcException(
                    DdcErrorKind.MonitorNotResponding,
                    $"{endpoint.Identity} did not accept the command (Windows error 0x{error:X8}).",
                    DdcHints.DdcCiDisabled);
            }
            return true;
        });
    }

    public override string? TryReadCapabilities(DisplayEndpoint endpoint) =>
        WithPhysicalMonitor(endpoint, handle =>
        {
            if (!Dxva2.GetCapabilitiesStringLength(handle, out var length) || length == 0 || length > 64 * 1024)
            {
                log.Debug($"dxva2: capabilities length unavailable for {endpoint} (0x{Marshal.GetLastPInvokeError():X8}).");
                return null;
            }

            var buffer = new byte[length];
            fixed (byte* p = buffer)
            {
                if (!Dxva2.CapabilitiesRequestAndCapabilitiesReply(handle, p, length))
                {
                    log.Debug($"dxva2: capabilities request failed for {endpoint} (0x{Marshal.GetLastPInvokeError():X8}).");
                    return null;
                }
            }
            return Encoding.ASCII.GetString(buffer).TrimEnd('\0');
        });

    public override VcpReading? TryReadVcp(DisplayEndpoint endpoint, byte vcpCode) =>
        WithPhysicalMonitor<VcpReading?>(endpoint, handle =>
            Dxva2.GetVCPFeatureAndVCPFeatureReply(handle, vcpCode, out _, out var current, out var maximum)
                ? new VcpReading((ushort)current, (ushort)maximum)
                : null);

    /// <summary>
    /// Physical monitor handles go stale when displays change, so each operation opens fresh
    /// handles and releases them afterwards.
    /// </summary>
    private T? WithPhysicalMonitor<T>(DisplayEndpoint endpoint, Func<nint, T?> action)
    {
        var target = (Target)endpoint.Native!;
        foreach (var monitor in User32.EnumerateMonitors())
        {
            if (!string.Equals(User32.GetDeviceName(monitor), target.GdiDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Dxva2.GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) || target.PhysicalIndex >= count)
            {
                break;
            }

            var handles = new Dxva2.PhysicalMonitor[count];
            fixed (Dxva2.PhysicalMonitor* p = handles)
            {
                if (!Dxva2.GetPhysicalMonitorsFromHMONITOR(monitor, count, p))
                {
                    break;
                }
                try
                {
                    return action(handles[target.PhysicalIndex].Handle);
                }
                finally
                {
                    Dxva2.DestroyPhysicalMonitors(count, p);
                }
            }
        }

        throw new DdcException(
            DdcErrorKind.MonitorNotFound,
            $"{endpoint.Identity} is no longer attached to {target.GdiDeviceName}.");
    }
}
