using System.Globalization;
using MonitorSwitcher.Core.Ddc;
using MonitorSwitcher.Core.Logging;
using MonitorSwitcher.Core.Monitors;
using MonitorSwitcher.Core.Storage;
using MonitorSwitcher.Core.Transports;

namespace MonitorSwitcher.Windows.Transports.Adl;

/// <summary>
/// Raw DDC/CI through the AMD driver (ADL2_Display_DDCBlockAccess_Get). Like the official ADL
/// DDC sample, the whole message is passed, starting with the 0x6E device address, which the
/// guard checks before every call. Monitors are identified from the EDID ADL reports for each
/// display, so no mapping guesswork is involved.
/// </summary>
/// <remarks>Not yet verified on AMD hardware with a DDC/CI monitor; reports are welcome.</remarks>
public sealed unsafe class AdlTransport(ILog log) : DdcTransport
{
    private readonly Lock _gate = new();
    private nint _context;

    private sealed record Target(int AdapterIndex, int DisplayIndex);

    public override string Id => "adl";

    public override string DisplayName => "AMD (ADL DDC)";

    public override bool SupportsRawI2C => true;

    public override int Priority => 20;

    public override TransportStatus Probe()
    {
        lock (_gate)
        {
            if (!AdlNative.TryLoad(out var error))
            {
                return TransportStatus.Unavailable(error!, "Only needed for monitors connected to an AMD GPU.");
            }
            if (!EnsureContext(out error))
            {
                return TransportStatus.Unavailable(error!, DdcHints.UpdateDriver);
            }

            int count = 0;
            return AdlNative.NumberOfAdaptersGet(_context, &count) == AdlNative.Ok && count > 0
                ? TransportStatus.Available($"{count} AMD display adapter entr{(count == 1 ? "y" : "ies")}")
                : TransportStatus.Unavailable("The AMD driver reports no adapters.");
        }
    }

    public override IReadOnlyList<DisplayEndpoint> Enumerate()
    {
        lock (_gate)
        {
            if (!AdlNative.TryLoad(out var error) || !EnsureContext(out error))
            {
                throw new DdcException(DdcErrorKind.DriverMissing, error!, DdcHints.UpdateDriver);
            }

            int count = 0;
            if (AdlNative.NumberOfAdaptersGet(_context, &count) != AdlNative.Ok || count <= 0)
            {
                return [];
            }

            var adapters = new AdlNative.AdapterInfo[count];
            fixed (AdlNative.AdapterInfo* p = adapters)
            {
                for (int i = 0; i < count; i++)
                {
                    p[i].Size = sizeof(AdlNative.AdapterInfo);
                }
                if (AdlNative.AdapterInfoGet(_context, p, sizeof(AdlNative.AdapterInfo) * count) != AdlNative.Ok)
                {
                    log.Warn("ADL2_Adapter_AdapterInfo_Get failed.");
                    return [];
                }
            }

            var result = new List<DisplayEndpoint>();
            var seen = new HashSet<(int, int)>();
            for (int i = 0; i < count; i++)
            {
                var adapter = adapters[i];
                if (adapter.Present == 0)
                {
                    continue;
                }

                int adapterIndex = adapter.AdapterIndex;
                string osName = AdlNative.ReadString(adapter.DisplayName, 256);
                string adapterName = AdlNative.ReadString(adapter.AdapterName, 256);

                int displays = 0;
                AdlNative.DisplayInfo* info = null;
                if (AdlNative.DisplayInfoGet(_context, adapterIndex, &displays, &info, 0) != AdlNative.Ok)
                {
                    continue;
                }

                try
                {
                    for (int d = 0; d < displays; d++)
                    {
                        var display = info[d];
                        // Each display is listed under every adapter entry of its GPU; keep it once.
                        if (display.Id.LogicalAdapterIndex != adapterIndex
                            || (display.InfoValue & AdlNative.DisplayConnected) == 0
                            || !seen.Add((adapterIndex, display.Id.LogicalIndex)))
                        {
                            continue;
                        }

                        var identity = ReadIdentity(adapterIndex, display.Id.LogicalIndex);
                        if (identity is null)
                        {
                            continue;
                        }

                        result.Add(new DisplayEndpoint(this, identity, $"{adapterName}, display {display.Id.LogicalIndex}", new Target(adapterIndex, display.Id.LogicalIndex))
                        {
                            OsDisplayName = osName.Length > 0 ? osName : null,
                        });
                    }
                }
                finally
                {
                    AdlNative.Free(info);
                }
            }
            return result;
        }
    }

    protected override void SendCore(DisplayEndpoint endpoint, SetVcpFrame frame)
    {
        var target = (Target)endpoint.Native!;
        var message = frame.BuildMessage();

        lock (_gate)
        {
            if (!EnsureContext(out var error))
            {
                throw new DdcException(DdcErrorKind.DriverMissing, error!, DdcHints.UpdateDriver);
            }

            DdcGuard.EnsureMessage(message); // first byte must be 0x6E, checksum must be valid
            int received = 0;
            int status;
            fixed (byte* send = message)
            {
                status = AdlNative.DdcBlockAccessGet(_context, target.AdapterIndex, target.DisplayIndex, 0, 0, message.Length, send, &received, null);
            }

            if (status != AdlNative.Ok)
            {
                throw new DdcException(
                    DdcErrorKind.MonitorNotResponding,
                    $"The AMD driver could not deliver the command to {endpoint.Identity} (ADL error {status}).",
                    DdcHints.DdcCiDisabled);
            }
        }
    }

    public override EndpointRecord? Describe(DisplayEndpoint endpoint) =>
        endpoint.Native is Target t ? new EndpointRecord { Transport = Id, Address = $"{t.AdapterIndex}:{t.DisplayIndex}" } : null;

    public override DisplayEndpoint? TryRestore(EndpointRecord record, MonitorIdentity identity)
    {
        var parts = record.Address.Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var adapter)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var display))
        {
            return null;
        }
        return new DisplayEndpoint(this, identity, $"AMD adapter {adapter}, display {display} (last known)", new Target(adapter, display))
        {
            IsRestoredFromCache = true,
        };
    }

    private MonitorIdentity? ReadIdentity(int adapterIndex, int displayIndex)
    {
        var edid = new AdlNative.EdidData { Size = sizeof(AdlNative.EdidData), BlockIndex = 0 };
        if (AdlNative.EdidDataGet(_context, adapterIndex, displayIndex, &edid) != AdlNative.Ok)
        {
            log.Debug($"ADL2_Display_EdidData_Get failed for adapter {adapterIndex}, display {displayIndex}.");
            return null;
        }
        return EdidParser.TryParse(new ReadOnlySpan<byte>(edid.Data, 256), out var info) ? MonitorIdentity.FromEdid(info!) : null;
    }

    private bool EnsureContext(out string? error)
    {
        error = null;
        if (_context != 0)
        {
            return true;
        }

        nint context = 0;
        int status = AdlNative.MainControlCreate(&AdlNative.Alloc, 1, &context);
        if (status != AdlNative.Ok || context == 0)
        {
            error = $"The AMD driver did not initialise (ADL error {status}).";
            return false;
        }
        _context = context;
        return true;
    }

    public override void Dispose()
    {
        lock (_gate)
        {
            if (_context != 0)
            {
                AdlNative.MainControlDestroy(_context);
                _context = 0;
            }
        }
        base.Dispose();
    }
}
