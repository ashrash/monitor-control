using System.Globalization;
using System.Text;
using MonitorSwitcher.Core.Ddc;
using MonitorSwitcher.Core.Logging;
using MonitorSwitcher.Core.Monitors;
using MonitorSwitcher.Core.Storage;
using MonitorSwitcher.Core.Transports;
using MonitorSwitcher.Windows.Displays;

namespace MonitorSwitcher.Windows.Transports.NvApi;

/// <summary>
/// Raw DDC/CI over the NVIDIA driver's I2C API (NvAPI_I2CWrite). Can send any source byte, so it
/// supports vendor protocols such as LG's. Each monitor is addressed by GPU + output id; nothing
/// is ever broadcast.
/// </summary>
public sealed unsafe class NvApiTransport(WindowsDisplayEnumerator displays, ILog log) : DdcTransport
{
    private readonly Lock _gate = new();
    private bool _initialized;
    private Gpu[] _gpus = [];

    private sealed record Gpu(int Index, nint Handle, string Name);

    private sealed record Target(int GpuIndex, uint OutputId);

    public override string Id => "nvapi";

    public override string DisplayName => "NVIDIA (NvAPI I2C)";

    public override bool SupportsRawI2C => true;

    public override int Priority => 10;

    public override TransportStatus Probe()
    {
        lock (_gate)
        {
            if (!NvApiNative.TryLoad(out var error))
            {
                return TransportStatus.Unavailable(error!, "Only needed for monitors connected to an NVIDIA GPU.");
            }

            try
            {
                EnsureInitialized(force: false);
            }
            catch (NvApiException ex)
            {
                return TransportStatus.Unavailable($"The NVIDIA driver did not initialise: {ex.Message}", DdcHints.UpdateDriver);
            }

            return _gpus.Length == 0
                ? TransportStatus.Unavailable("The NVIDIA driver is installed but reports no NVIDIA GPU.")
                : TransportStatus.Available(string.Join(", ", _gpus.Select(g => g.Name)));
        }
    }

    public override IReadOnlyList<DisplayEndpoint> Enumerate()
    {
        lock (_gate)
        {
            EnsureInitialized(force: false);
            var osNames = MapOsDisplayNames();
            var result = new List<DisplayEndpoint>();

            foreach (var gpu in _gpus)
            {
                uint mask = 0;
                int status = NvApiNative.GetConnectedOutputs(gpu.Handle, &mask);
                if (status != NvApiNative.Ok)
                {
                    log.Warn($"NvAPI_GPU_GetConnectedOutputs on {gpu.Name} failed: {NvApiNative.ErrorMessage(status)}");
                    continue;
                }

                for (int bit = 0; bit < 32; bit++)
                {
                    uint output = 1u << bit;
                    if ((mask & output) == 0)
                    {
                        continue;
                    }

                    var identity = ReadIdentity(gpu, output);
                    if (identity is null)
                    {
                        continue;
                    }

                    result.Add(new DisplayEndpoint(this, identity, Connector(gpu, output), new Target(gpu.Index, output))
                    {
                        OsDisplayName = osNames.GetValueOrDefault((gpu.Handle, output)),
                    });
                }
            }
            return result;
        }
    }

    protected override void SendCore(DisplayEndpoint endpoint, SetVcpFrame frame)
    {
        var target = (Target)endpoint.Native!;
        var payload = frame.BuildPayload();
        byte register = frame.SourceAddress;

        lock (_gate)
        {
            int status = Write(target, register, payload);
            if (status is NvApiNative.HandleInvalidated or NvApiNative.InvalidHandle)
            {
                // The driver was reset (e.g. a driver update or GPU sleep); re-enumerate once.
                log.Info("NvAPI handles were invalidated; re-initialising.");
                EnsureInitialized(force: true);
                status = Write(target, register, payload);
            }

            if (status != NvApiNative.Ok)
            {
                throw new DdcException(
                    DdcErrorKind.MonitorNotResponding,
                    $"The NVIDIA driver could not deliver the command to {endpoint.Identity}: {NvApiNative.ErrorMessage(status)}.",
                    DdcHints.DdcCiDisabled);
            }
        }
    }

    private int Write(Target target, byte register, byte[] payload)
    {
        EnsureInitialized(force: false);
        if (target.GpuIndex >= _gpus.Length)
        {
            throw new DdcException(DdcErrorKind.MonitorNotFound, $"NVIDIA GPU #{target.GpuIndex} is no longer present.");
        }

        fixed (byte* data = payload)
        {
            var info = new NvApiNative.I2CInfoV3
            {
                Version = NvApiNative.MakeVersion<NvApiNative.I2CInfoV3>(3),
                DisplayMask = target.OutputId,
                IsDdcPort = 1,
                DeviceAddress = DdcConstants.DisplayDeviceAddress,
                RegisterAddress = &register,
                RegisterAddressSize = 1,
                Data = data,
                DataSize = (uint)payload.Length,
                I2CSpeed = 0xFFFF,
                I2CSpeedKhz = 0,
            };

            DdcGuard.EnsureDeviceAddress(info.DeviceAddress);
            DdcGuard.EnsureMessage([info.DeviceAddress, register, .. payload]);
            return NvApiNative.I2CWrite(_gpus[target.GpuIndex].Handle, &info);
        }
    }

    public override EndpointRecord? Describe(DisplayEndpoint endpoint) =>
        endpoint.Native is Target t
            ? new EndpointRecord { Transport = Id, Address = $"{t.GpuIndex}:0x{t.OutputId:X}" }
            : null;

    public override DisplayEndpoint? TryRestore(EndpointRecord record, MonitorIdentity identity)
    {
        var parts = record.Address.Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var gpuIndex)
            || !uint.TryParse(parts[1].Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var output))
        {
            return null;
        }

        lock (_gate)
        {
            EnsureInitialized(force: false);
            if (gpuIndex >= _gpus.Length)
            {
                return null;
            }

            var gpu = _gpus[gpuIndex];
            if (NvApiNative.Has(NvApiNative.Ids.GetAllOutputs))
            {
                uint all = 0;
                if (NvApiNative.GetAllOutputs(gpu.Handle, &all) == NvApiNative.Ok && (all & output) == 0)
                {
                    return null;
                }
            }

            return new DisplayEndpoint(this, identity, Connector(gpu, output) + " (last known)", new Target(gpuIndex, output))
            {
                IsRestoredFromCache = true,
            };
        }
    }

    private void EnsureInitialized(bool force)
    {
        if (_initialized && !force)
        {
            return;
        }

        if (!NvApiNative.TryLoad(out var error))
        {
            throw new DdcException(DdcErrorKind.DriverMissing, error!, DdcHints.UpdateDriver);
        }

        int status = NvApiNative.Initialize();
        if (status != NvApiNative.Ok)
        {
            throw new NvApiException(NvApiNative.ErrorMessage(status), status);
        }

        var handles = stackalloc nint[NvApiNative.MaxPhysicalGpus];
        int count = 0;
        status = NvApiNative.EnumPhysicalGpus(handles, &count);
        if (status == NvApiNative.NvidiaDeviceNotFound)
        {
            count = 0;
        }
        else if (status != NvApiNative.Ok)
        {
            throw new NvApiException(NvApiNative.ErrorMessage(status), status);
        }

        var gpus = new Gpu[count];
        var name = stackalloc byte[64];
        for (int i = 0; i < count; i++)
        {
            new Span<byte>(name, 64).Clear();
            var gpuName = NvApiNative.Has(NvApiNative.Ids.GetFullName) && NvApiNative.GetFullName(handles[i], name) == NvApiNative.Ok
                ? NvApiNative.ReadShortString(name)
                : $"NVIDIA GPU {i}";
            gpus[i] = new Gpu(i, handles[i], gpuName);
        }

        _gpus = gpus;
        _initialized = true;
        log.Debug($"NvAPI initialised: {string.Join(", ", gpus.Select(g => g.Name))}");
    }

    private MonitorIdentity? ReadIdentity(Gpu gpu, uint output)
    {
        var edid = new NvApiNative.EdidV3 { Version = NvApiNative.MakeVersion<NvApiNative.EdidV3>(3) };
        int status = NvApiNative.GetEdid(gpu.Handle, output, &edid);
        if (status != NvApiNative.Ok)
        {
            log.Debug($"NvAPI_GPU_GetEDID {Connector(gpu, output)} failed: {NvApiNative.ErrorMessage(status)}");
            return null;
        }

        var bytes = new ReadOnlySpan<byte>(edid.Data, 256);
        if (!EdidParser.TryParse(bytes, out var info))
        {
            log.Debug($"EDID from {Connector(gpu, output)} could not be parsed.");
            return null;
        }
        return MonitorIdentity.FromEdid(info!);
    }

    /// <summary>Maps (GPU handle, output id) to Windows names like \\.\DISPLAY1, for friendlier listings.</summary>
    private Dictionary<(nint, uint), string> MapOsDisplayNames()
    {
        var map = new Dictionary<(nint, uint), string>();
        if (!NvApiNative.Has(NvApiNative.Ids.GetDisplayIdByDisplayName) || !NvApiNative.Has(NvApiNative.Ids.GetGpuAndOutputIdFromDisplayId))
        {
            return map;
        }

        foreach (var display in displays.GetActiveDisplays())
        {
            var name = Encoding.ASCII.GetBytes(display.GdiDeviceName + "\0");
            uint displayId = 0;
            nint gpu = 0;
            uint output = 0;
            fixed (byte* p = name)
            {
                if (NvApiNative.GetDisplayIdByDisplayName(p, &displayId) != NvApiNative.Ok)
                {
                    continue; // not driven by an NVIDIA GPU
                }
            }
            if (NvApiNative.GetGpuAndOutputIdFromDisplayId(displayId, &gpu, &output) == NvApiNative.Ok)
            {
                map[(gpu, output)] = display.GdiDeviceName;
            }
        }
        return map;
    }

    private static string Connector(Gpu gpu, uint output) => $"{gpu.Name}, output 0x{output:X}";
}
