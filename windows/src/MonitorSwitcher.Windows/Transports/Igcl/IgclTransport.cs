using MonitorSwitcher.Core.Ddc;
using MonitorSwitcher.Core.Logging;
using MonitorSwitcher.Core.Transports;
using MonitorSwitcher.Windows.Displays;

namespace MonitorSwitcher.Windows.Transports.Igcl;

/// <summary>
/// Raw DDC/CI through the Intel Graphics Control Library (ctlI2CAccess), filled in the same way
/// as Intel's I2C sample: Address 0x6E, Offset = source byte, Data = payload.
/// </summary>
/// <remarks>
/// EXPERIMENTAL and off by default (set MONITORSWITCHER_ENABLE_IGCL=1 to enable). It has not been
/// tested on Intel hardware, and outputs are matched to monitors through the Windows target id
/// reported by ctlGetDisplayProperties, which needs confirming. Monitors on Intel GPUs still work
/// with the standard protocol through dxva2 without this.
/// </remarks>
public sealed unsafe class IgclTransport(WindowsDisplayEnumerator displays, ILog log) : DdcTransport
{
    public const string EnableVariable = "MONITORSWITCHER_ENABLE_IGCL";

    private static readonly Guid AppId = new("6f1b0c52-8d7e-4c55-9a43-1f0d3e2b7a91");
    private readonly Lock _gate = new();
    private nint _api;

    public static bool IsEnabled => Environment.GetEnvironmentVariable(EnableVariable) == "1";

    public override string Id => "igcl";

    public override string DisplayName => "Intel (IGCL I2C, experimental)";

    public override bool SupportsRawI2C => true;

    public override int Priority => 30;

    public override TransportStatus Probe()
    {
        if (!IsEnabled)
        {
            return TransportStatus.Unavailable($"Experimental; set {EnableVariable}=1 to enable. Intel GPUs still work with the standard protocol via dxva2.");
        }

        lock (_gate)
        {
            if (!IgclNative.TryLoad(out var error))
            {
                return TransportStatus.Unavailable(error!, "Only needed for monitors connected to an Intel GPU.");
            }
            return EnsureInitialized(out error)
                ? TransportStatus.Available("Intel Graphics Control Library")
                : TransportStatus.Unavailable(error!, DdcHints.UpdateDriver);
        }
    }

    public override IReadOnlyList<DisplayEndpoint> Enumerate()
    {
        if (!IsEnabled)
        {
            return [];
        }

        lock (_gate)
        {
            if (!IgclNative.TryLoad(out var error) || !EnsureInitialized(out error))
            {
                throw new DdcException(DdcErrorKind.DriverMissing, error!, DdcHints.UpdateDriver);
            }

            // Windows target ids identify the monitor; only use ids that are unique on this machine.
            var byTarget = displays.GetActiveDisplays()
                .Where(d => d.Identity is not null)
                .GroupBy(d => d.TargetId)
                .Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.Single());

            var result = new List<DisplayEndpoint>();
            foreach (var device in Enumerate(n => IgclNative.EnumerateDevices(_api, n, null), (n, a) => IgclNative.EnumerateDevices(_api, n, a)))
            {
                foreach (var output in Enumerate(n => IgclNative.EnumerateDisplayOutputs(device, n, null), (n, a) => IgclNative.EnumerateDisplayOutputs(device, n, a)))
                {
                    var props = new IgclNative.DisplayProperties { Size = (uint)sizeof(IgclNative.DisplayProperties) };
                    if (IgclNative.GetDisplayProperties(output, &props) != IgclNative.Success
                        || (props.DisplayConfigFlags & IgclNative.DisplayAttached) == 0)
                    {
                        continue;
                    }

                    if (!byTarget.TryGetValue(props.OsDisplayEncoderHandle.WindowsDisplayEncoderId, out var display))
                    {
                        log.Debug($"IGCL output with encoder id {props.OsDisplayEncoderHandle.WindowsDisplayEncoderId} matches no unique Windows display; skipped.");
                        continue;
                    }

                    result.Add(new DisplayEndpoint(this, display.Identity!, $"Intel output {props.OsDisplayEncoderHandle.WindowsDisplayEncoderId}", output)
                    {
                        OsDisplayName = display.GdiDeviceName,
                    });
                }
            }
            return result;
        }
    }

    protected override void SendCore(DisplayEndpoint endpoint, SetVcpFrame frame)
    {
        var output = (nint)endpoint.Native!;
        var payload = frame.BuildPayload();

        lock (_gate)
        {
            var args = new IgclNative.I2CAccessArgs
            {
                Size = (uint)sizeof(IgclNative.I2CAccessArgs),
                OpType = IgclNative.OperationWrite,
                Address = DdcConstants.DisplayDeviceAddress,
                Offset = frame.SourceAddress,
                DataSize = (uint)payload.Length,
            };
            for (int i = 0; i < payload.Length; i++)
            {
                args.Data[i] = payload[i];
            }

            DdcGuard.EnsureDeviceAddress(checked((byte)args.Address));
            DdcGuard.EnsureMessage([(byte)args.Address, (byte)args.Offset, .. payload]);
            int status = IgclNative.I2CAccess(output, &args);
            if (status != IgclNative.Success)
            {
                throw new DdcException(
                    DdcErrorKind.MonitorNotResponding,
                    $"The Intel driver could not deliver the command to {endpoint.Identity} (IGCL result 0x{status:X8}).",
                    DdcHints.DdcCiDisabled);
            }
        }
    }

    private bool EnsureInitialized(out string? error)
    {
        error = null;
        if (_api != 0)
        {
            return true;
        }

        var args = new IgclNative.InitArgs
        {
            Size = (uint)sizeof(IgclNative.InitArgs),
            AppVersion = IgclNative.MakeVersion(1, 1),
        };
        var guid = AppId.ToByteArray();
        args.ApplicationUid.Data1 = BitConverter.ToUInt32(guid, 0);
        args.ApplicationUid.Data2 = BitConverter.ToUInt16(guid, 4);
        args.ApplicationUid.Data3 = BitConverter.ToUInt16(guid, 6);
        for (int i = 0; i < 8; i++)
        {
            args.ApplicationUid.Data4[i] = guid[8 + i];
        }

        nint api = 0;
        int status = IgclNative.Init(&args, &api);
        if (status != IgclNative.Success || api == 0)
        {
            error = $"The Intel driver did not initialise (IGCL result 0x{status:X8}).";
            return false;
        }
        _api = api;
        return true;
    }

    private static nint[] Enumerate(CountFn count, FillFn fill)
    {
        uint n = 0;
        if (count(&n) != IgclNative.Success || n == 0)
        {
            return [];
        }
        var handles = new nint[n];
        fixed (nint* p = handles)
        {
            return fill(&n, p) == IgclNative.Success ? handles[..(int)Math.Min(n, (uint)handles.Length)] : [];
        }
    }

    private delegate int CountFn(uint* count);

    private delegate int FillFn(uint* count, nint* handles);

    public override void Dispose()
    {
        lock (_gate)
        {
            if (_api != 0)
            {
                IgclNative.Close(_api);
                _api = 0;
            }
        }
        base.Dispose();
    }
}
