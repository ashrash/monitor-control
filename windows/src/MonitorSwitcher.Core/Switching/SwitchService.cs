using System.Diagnostics;
using MonitorSwitcher.Core.Ddc;
using MonitorSwitcher.Core.Logging;
using MonitorSwitcher.Core.Monitors;
using MonitorSwitcher.Core.Profiles;
using MonitorSwitcher.Core.Protocols;
using MonitorSwitcher.Core.Storage;
using MonitorSwitcher.Core.Transports;

namespace MonitorSwitcher.Core.Switching;

public sealed record TransportReport(DdcTransport Transport, TransportStatus Status);

/// <summary>
/// The one entry point UIs use to switch inputs. Ties the layers together:
/// profile (which codes are allowed) → protocol (which bytes) → transport (how to deliver).
/// Every value is checked against the monitor's profile, or for the setup wizard against the
/// protocol's candidate list, before anything is sent.
/// </summary>
public sealed class SwitchService(
    IReadOnlyList<DdcTransport> transports,
    ProfileCatalog catalog,
    JsonFileStore<AppState> stateStore,
    ICrossProcessLock busLock,
    ILog log)
{
    private static readonly TimeSpan BusTimeout = TimeSpan.FromSeconds(5);
    private IReadOnlyList<TransportReport>? _reports;

    public ProfileCatalog Catalog => catalog;

    /// <summary>Swaps in a freshly loaded catalog, e.g. after the setup wizard saves a user profile.</summary>
    public void ReloadProfiles(ProfileCatalog updated) => catalog = updated;

    public JsonFileStore<AppState> State => stateStore;

    /// <summary>Availability of every transport. Cached; pass <paramref name="refresh"/> after driver changes.</summary>
    public IReadOnlyList<TransportReport> ProbeTransports(bool refresh = false)
    {
        if (_reports is not null && !refresh)
        {
            return _reports;
        }

        var reports = new List<TransportReport>();
        foreach (var transport in transports.OrderBy(t => t.Priority))
        {
            TransportStatus status;
            try
            {
                status = transport.Probe();
            }
            catch (Exception ex)
            {
                status = TransportStatus.Unavailable($"Probe failed: {ex.Message}");
            }
            log.Debug($"Transport {transport.Id}: {(status.IsAvailable ? "available" : "unavailable")} - {status.Summary}");
            reports.Add(new TransportReport(transport, status));
        }
        return _reports = reports;
    }

    /// <summary>Finds every reachable monitor and resolves its profile.</summary>
    /// <param name="settings">Used for forced profile ids; optional.</param>
    /// <param name="readCapabilities">Allow a (slow, cached) capabilities read when the product code has no profile.</param>
    public IReadOnlyList<ResolvedMonitor> DiscoverMonitors(AppSettings? settings = null, bool readCapabilities = true)
    {
        var available = ProbeTransports().Where(r => r.Status.IsAvailable).Select(r => r.Transport).ToList();
        if (available.Count == 0)
        {
            var details = string.Join(" ", ProbeTransports().Select(r => $"{r.Transport.DisplayName}: {r.Status.Summary}."));
            throw new DdcException(
                DdcErrorKind.NoSupportedTransport,
                $"No supported graphics driver interface was found. {details}",
                DdcHints.UpdateDriver);
        }

        var endpoints = new List<DisplayEndpoint>();
        foreach (var transport in available)
        {
            try
            {
                endpoints.AddRange(transport.Enumerate());
            }
            catch (Exception ex)
            {
                log.Warn($"Listing monitors through {transport.Id} failed.", ex);
            }
        }

        var state = stateStore.Load();
        var monitors = new List<ResolvedMonitor>();
        foreach (var group in GroupByMonitor(endpoints))
        {
            var ordered = group.OrderBy(e => e.Transport.Priority).ToList();
            var identity = ordered
                .Select(e => e.Identity)
                .OrderByDescending(HasSerial)
                .ThenByDescending(i => i.ModelName is not null)
                .First();
            monitors.Add(Resolve(identity, ordered, state, settings, readCapabilities));
        }

        // A monitor showing another computer may vanish from the OS. If we have reached it
        // before, try its last known address so the user can still switch back.
        foreach (var (stableId, monitorState) in state.Monitors)
        {
            if (monitors.Any(m => m.StableId == stableId) || monitorState is not { LastEndpoint: { } record, Identity: { } identity })
            {
                continue;
            }

            var transport = available.FirstOrDefault(t => t.Id == record.Transport);
            DisplayEndpoint? restored = null;
            try
            {
                restored = transport?.TryRestore(record, identity);
            }
            catch (Exception ex)
            {
                log.Warn($"Could not restore {stableId} from {record.Transport} {record.Address}.", ex);
            }

            if (restored is not null)
            {
                log.Info($"{identity} is not listed by the OS; using its last known address {record.Transport} {record.Address}.");
                monitors.Add(Resolve(identity, [restored], state, settings, readCapabilities: false));
            }
        }

        return monitors
            .OrderBy(m => m.OsDisplayName ?? "~", StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool HasSerial(MonitorIdentity identity) =>
        identity.SerialNumber != 0 || !string.IsNullOrWhiteSpace(identity.SerialString);

    /// <summary>
    /// Groups endpoints from all transports into physical monitors. Endpoints whose EDID came back
    /// without a serial (Windows' cached EDID can be briefly unavailable during a hotplug) join the
    /// serial-bearing monitor of the same model on the same OS display, or the only one of that model.
    /// </summary>
    internal static IReadOnlyList<List<DisplayEndpoint>> GroupByMonitor(IEnumerable<DisplayEndpoint> endpoints)
    {
        var result = new List<List<DisplayEndpoint>>();
        foreach (var model in endpoints.GroupBy(e => (e.Identity.ManufacturerId, e.Identity.ProductCode)))
        {
            var withSerial = model
                .Where(e => HasSerial(e.Identity))
                .GroupBy(e => e.Identity.StableId, StringComparer.Ordinal)
                .Select(g => g.ToList())
                .ToList();

            var orphans = new List<DisplayEndpoint>();
            foreach (var endpoint in model.Where(e => !HasSerial(e.Identity)))
            {
                var sameDisplay = endpoint.OsDisplayName is null
                    ? null
                    : withSerial.Where(g => g.Any(e => string.Equals(e.OsDisplayName, endpoint.OsDisplayName, StringComparison.OrdinalIgnoreCase))).ToList();

                if (sameDisplay is { Count: 1 })
                {
                    sameDisplay[0].Add(endpoint);
                }
                else if (withSerial.Count == 1)
                {
                    withSerial[0].Add(endpoint);
                }
                else
                {
                    orphans.Add(endpoint);
                }
            }

            result.AddRange(withSerial);
            // Several serial-less monitors of one model: keep them apart by OS display or connector.
            result.AddRange(orphans
                .GroupBy(e => e.OsDisplayName ?? $"{e.Transport.Id}:{e.ConnectorId}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.ToList()));
        }
        return result;
    }

    private ResolvedMonitor Resolve(
        MonitorIdentity identity,
        IReadOnlyList<DisplayEndpoint> endpoints,
        AppState state,
        AppSettings? settings,
        bool readCapabilities)
    {
        var forcedProfile = settings?.FindMonitor(identity.StableId)?.ProfileId;
        state.Monitors.TryGetValue(identity.StableId, out var monitorState);
        var capabilities = monitorState?.Capabilities is { } cached ? CapabilitiesParser.Parse(cached) : null;

        var match = catalog.Resolve(identity, capabilities, forcedProfile);
        if (match.Quality < MatchQuality.CapabilitiesModel && capabilities is null && readCapabilities)
        {
            capabilities = ReadAndCacheCapabilities(identity, endpoints);
            if (capabilities is not null)
            {
                match = catalog.Resolve(identity, capabilities, forcedProfile);
            }
        }

        return new ResolvedMonitor
        {
            Identity = identity,
            Endpoints = endpoints,
            Profile = match.Profile,
            Quality = match.Quality,
            Capabilities = capabilities,
        };
    }

    /// <summary>Reads the capabilities string through the first endpoint that supports it, and caches it.</summary>
    public MonitorCapabilities? ReadAndCacheCapabilities(MonitorIdentity identity, IReadOnlyList<DisplayEndpoint> endpoints)
    {
        foreach (var endpoint in endpoints)
        {
            string? raw;
            try
            {
                using (busLock.Acquire(BusTimeout))
                {
                    raw = endpoint.Transport.TryReadCapabilities(endpoint);
                }
            }
            catch (Exception ex)
            {
                log.Warn($"Reading capabilities of {identity} through {endpoint.Transport.Id} failed.", ex);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(raw))
            {
                log.Info($"Capabilities of {identity}: {raw}");
                stateStore.Update(s =>
                {
                    var m = s.GetOrAdd(identity.StableId);
                    m.Capabilities = raw;
                    m.Identity ??= identity;
                });
                return CapabilitiesParser.Parse(raw);
            }
        }
        return null;
    }

    /// <summary>Switches to an input listed in the monitor's profile.</summary>
    /// <param name="settings">Optional; lets the service explain why switching to this computer's own input can't work.</param>
    public SwitchOutcome SwitchTo(ResolvedMonitor monitor, string inputId, MonitorSettings? settings = null)
    {
        var input = monitor.Profile.FindInput(inputId) ?? throw new DdcException(
            DdcErrorKind.InputNotInProfile,
            $"'{inputId}' is not an input of {monitor.Name}. Choose one of: {InputResolver.Describe(monitor.Profile.Inputs, null)}.");

        if (monitor.Profile.EffectiveQuirks.IgnoresCommandsWhenInactive
            && string.Equals(settings?.LocalInputId, input.Id, StringComparison.OrdinalIgnoreCase)
            && IsAnswering(monitor) == false)
        {
            throw new DdcException(
                DdcErrorKind.MonitorOnOtherInput,
                $"{monitor.Name} is showing another input and ignores this computer until it is switched back.",
                "Switch back from the other computer (Monitor Switcher runs on macOS too) or with the monitor's buttons.");
        }

        var protocol = monitor.Protocol;
        var outcome = Deliver(monitor, protocol, input.CodeValue, monitor.Profile.EffectiveQuirks, input.Id, input.Name);
        Remember(monitor, outcome.Endpoint, input.Id);
        return outcome;
    }

    /// <summary>Switches between the monitor's two toggle inputs.</summary>
    public SwitchOutcome Toggle(ResolvedMonitor monitor, MonitorSettings? settings)
    {
        return SwitchTo(monitor, ToggleTarget(monitor, settings), settings);
    }

    public string ToggleTarget(ResolvedMonitor monitor, MonitorSettings? settings)
    {
        var pair = ToggleLogic.ToggleInputs(monitor.Profile, settings) ?? throw new DdcException(
            DdcErrorKind.Configuration,
            $"No toggle inputs are set for {monitor.Name}.",
            "Choose the two inputs to toggle between in Settings.");

        stateStore.Load().Monitors.TryGetValue(monitor.StableId, out var monitorState);
        return ToggleLogic.NextInput(
            pair.A,
            pair.B,
            monitorState?.LastInputId,
            settings?.LocalInputId,
            monitor.Profile.EffectiveQuirks.IgnoresCommandsWhenInactive);
    }

    /// <summary>
    /// Does the monitor answer a DDC/CI read right now? true/false, or null when no transport can
    /// read. For monitors with <see cref="ProfileQuirks.IgnoresCommandsWhenInactive"/>, false means
    /// it is showing another input.
    /// </summary>
    public bool? IsAnswering(ResolvedMonitor monitor)
    {
        var readers = monitor.Endpoints.Where(e => e.Transport.SupportsReads).ToList();
        if (readers.Count == 0)
        {
            return null;
        }

        using (busLock.Acquire(BusTimeout))
        {
            foreach (var endpoint in readers)
            {
                try
                {
                    if (endpoint.Transport.TryReadVcp(endpoint, 0x10) is not null)
                    {
                        return true;
                    }
                }
                catch (DdcException ex)
                {
                    log.Debug($"Read through {endpoint.Transport.Id} failed: {ex.Message}");
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Setup wizard only: sends a code that may not be in the profile yet. The code must be one of
    /// the protocol's own candidates for this monitor, or a code from the monitor's profile when
    /// the profile uses this protocol, so the wizard can't send arbitrary values.
    /// </summary>
    public SwitchOutcome SendCandidate(ResolvedMonitor monitor, InputProtocol protocol, InputCandidate candidate)
    {
        bool allowed = protocol.Candidates.Any(c => c.Code == candidate.Code)
            || protocol.CandidatesFor(monitor.Capabilities).Any(c => c.Code == candidate.Code)
            || (string.Equals(protocol.Id, monitor.Profile.Protocol, StringComparison.OrdinalIgnoreCase)
                && monitor.Profile.Inputs.Any(i => i.CodeValue == candidate.Code));
        if (!allowed)
        {
            throw new DdcSafetyException($"{Hex.Format(candidate.Code)} is not a {protocol.DisplayName} candidate for {candidate.Name}.");
        }

        var outcome = Deliver(monitor, protocol, candidate.Code, ProfileQuirks.Default, candidate.InputId, candidate.Name);
        Remember(monitor, outcome.Endpoint, lastInputId: null);
        return outcome;
    }

    private SwitchOutcome Deliver(
        ResolvedMonitor monitor,
        InputProtocol protocol,
        ushort code,
        ProfileQuirks quirks,
        string inputId,
        string inputName)
    {
        var endpoint = monitor.EndpointFor(protocol) ?? throw new DdcException(
            DdcErrorKind.UnsupportedByTransport,
            $"{monitor.Name} needs the {protocol.DisplayName} command, which only the NVIDIA, AMD or Intel driver interfaces can send, and none of them can reach this monitor.",
            "Connect the monitor to a GPU from one of those vendors, or run the setup wizard to try the standard command.");

        var steps = protocol.BuildSwitch(code);
        var stopwatch = Stopwatch.StartNew();
        int attempts = 0;

        using (busLock.Acquire(BusTimeout))
        {
            while (true)
            {
                attempts++;
                try
                {
                    foreach (var step in steps)
                    {
                        endpoint.Transport.Send(endpoint, step.Frame);
                        if (step.DelayAfterMs > 0)
                        {
                            Thread.Sleep(step.DelayAfterMs);
                        }
                    }
                    break;
                }
                catch (DdcException ex) when (ex.Kind is DdcErrorKind.TransportFailure or DdcErrorKind.MonitorNotResponding
                                              && attempts <= quirks.Retries)
                {
                    log.Warn($"Attempt {attempts} to switch {monitor.Name} to {inputName} failed; retrying.", ex);
                    Thread.Sleep(quirks.RetryDelayMs);
                }
            }

            if (quirks.PostSwitchDelayMs > 0)
            {
                Thread.Sleep(quirks.PostSwitchDelayMs);
            }
        }

        stopwatch.Stop();
        log.Info($"Switched {monitor.Name} [{monitor.StableId}] to {inputName} ({inputId}, {Hex.Format(code)}) via {endpoint.Transport.Id} {endpoint.ConnectorId}: {string.Join(" ; ", steps.Select(s => s.Frame))} in {stopwatch.ElapsedMilliseconds} ms, {attempts} attempt(s).");
        return new SwitchOutcome(monitor, inputId, inputName, code, endpoint, attempts, stopwatch.Elapsed);
    }

    private void Remember(ResolvedMonitor monitor, DisplayEndpoint endpoint, string? lastInputId)
    {
        try
        {
            var record = endpoint.Transport.Describe(endpoint);
            stateStore.Update(s =>
            {
                var m = s.GetOrAdd(monitor.StableId);
                m.Identity = monitor.Identity;
                if (record is not null)
                {
                    m.LastEndpoint = record;
                }
                if (lastInputId is not null)
                {
                    m.LastInputId = lastInputId;
                    m.LastSwitchedAt = DateTimeOffset.Now;
                }
            });
        }
        catch (Exception ex)
        {
            // The switch already happened; failing to save state must not turn it into an error.
            log.Warn("Could not save state after switching.", ex);
        }
    }
}
