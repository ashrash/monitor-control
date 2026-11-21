using System.Buffers.Binary;
using Microsoft.Win32;
using MonitorSwitcher.Core.Logging;
using MonitorSwitcher.Core.Monitors;
using MonitorSwitcher.Windows.Native;

namespace MonitorSwitcher.Windows.Displays;

/// <summary>An active Windows display path: GDI name (\\.\DISPLAY1) plus the monitor's identity.</summary>
public sealed record WindowsDisplay(
    string GdiDeviceName,
    string? FriendlyName,
    string? DevicePath,
    MonitorIdentity? Identity,
    uint TargetId = 0);

/// <summary>Lists active displays with QueryDisplayConfig and reads each monitor's EDID from the registry.</summary>
public sealed unsafe class WindowsDisplayEnumerator(ILog log)
{
    public IReadOnlyList<WindowsDisplay> GetActiveDisplays()
    {
        DisplayConfig.PathInfo[] paths;
        uint pathCount;
        int status;
        int tries = 0;
        do
        {
            status = DisplayConfig.GetDisplayConfigBufferSizes(DisplayConfig.QdcOnlyActivePaths, out pathCount, out var modeCount);
            if (status != DisplayConfig.ErrorSuccess)
            {
                log.Warn($"GetDisplayConfigBufferSizes failed with {status}.");
                return [];
            }

            paths = new DisplayConfig.PathInfo[pathCount];
            var modes = new DisplayConfig.ModeInfo[modeCount];
            fixed (DisplayConfig.PathInfo* p = paths)
            fixed (DisplayConfig.ModeInfo* m = modes)
            {
                status = DisplayConfig.QueryDisplayConfig(DisplayConfig.QdcOnlyActivePaths, ref pathCount, p, ref modeCount, m, 0);
            }
        }
        while (status == DisplayConfig.ErrorInsufficientBuffer && ++tries < 5);

        if (status != DisplayConfig.ErrorSuccess)
        {
            log.Warn($"QueryDisplayConfig failed with {status}.");
            return [];
        }

        var result = new List<WindowsDisplay>();
        for (int i = 0; i < pathCount; i++)
        {
            var path = paths[i];
            var gdiName = GetSourceName(path.SourceInfo.AdapterId, path.SourceInfo.Id);
            if (gdiName is null)
            {
                continue;
            }

            var target = GetTargetName(path.TargetInfo.AdapterId, path.TargetInfo.Id);
            MonitorIdentity? identity = null;
            if (target is { } t)
            {
                identity = IdentityFromRegistry(t.DevicePath, t.FriendlyName) ?? IdentityFromIds(t.ManufacturerId, t.ProductCode, t.FriendlyName);
            }
            result.Add(new WindowsDisplay(gdiName, target?.FriendlyName, target?.DevicePath, identity, path.TargetInfo.Id));
        }
        return result;
    }

    private static string? GetSourceName(Luid adapter, uint id)
    {
        var request = new DisplayConfig.SourceDeviceName();
        request.Header.Type = DisplayConfig.DeviceInfoGetSourceName;
        request.Header.Size = (uint)sizeof(DisplayConfig.SourceDeviceName);
        request.Header.AdapterId = adapter;
        request.Header.Id = id;
        return DisplayConfig.DisplayConfigGetDeviceInfo(&request.Header) == DisplayConfig.ErrorSuccess
            ? new string(request.ViewGdiDeviceName)
            : null;
    }

    private static (string? FriendlyName, string? DevicePath, ushort ManufacturerId, ushort ProductCode)? GetTargetName(Luid adapter, uint id)
    {
        var request = new DisplayConfig.TargetDeviceName();
        request.Header.Type = DisplayConfig.DeviceInfoGetTargetName;
        request.Header.Size = (uint)sizeof(DisplayConfig.TargetDeviceName);
        request.Header.AdapterId = adapter;
        request.Header.Id = id;
        if (DisplayConfig.DisplayConfigGetDeviceInfo(&request.Header) != DisplayConfig.ErrorSuccess)
        {
            return null;
        }

        var friendly = new string(request.MonitorFriendlyDeviceName);
        var devicePath = new string(request.MonitorDevicePath);
        return (friendly.Length > 0 ? friendly : null, devicePath.Length > 0 ? devicePath : null,
            request.EdidManufactureId, request.EdidProductCodeId);
    }

    private MonitorIdentity? IdentityFromRegistry(string? devicePath, string? friendlyName)
    {
        var edid = ReadEdidFromRegistry(devicePath);
        if (edid is null || !EdidParser.TryParse(edid, out var info))
        {
            return null;
        }
        var identity = MonitorIdentity.FromEdid(info!);
        return identity.ModelName is null && friendlyName is not null ? identity with { ModelName = friendlyName } : identity;
    }

    private static MonitorIdentity? IdentityFromIds(ushort manufacturerId, ushort productCode, string? friendlyName)
    {
        var maker = EdidParser.DecodeManufacturerId(BinaryPrimitives.ReverseEndianness(manufacturerId))
            ?? EdidParser.DecodeManufacturerId(manufacturerId);
        return maker is null ? null : new MonitorIdentity(maker, productCode, ModelName: friendlyName);
    }

    /// <summary>
    /// Reads the EDID Windows cached for a monitor device path such as
    /// <c>\\?\DISPLAY#GSM774F#5&amp;2d1&amp;0&amp;UID4352#{e6f07b5f-...}</c>.
    /// </summary>
    internal byte[]? ReadEdidFromRegistry(string? devicePath)
    {
        if (string.IsNullOrEmpty(devicePath))
        {
            return null;
        }

        var parts = devicePath.Replace(@"\\?\", "", StringComparison.Ordinal).Split('#');
        if (parts.Length < 3)
        {
            return null;
        }

        var keyPath = $@"SYSTEM\CurrentControlSet\Enum\{parts[0]}\{parts[1]}\{parts[2]}\Device Parameters";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            return key?.GetValue("EDID") as byte[];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            log.Debug($"Could not read EDID from {keyPath}: {ex.Message}");
            return null;
        }
    }
}
