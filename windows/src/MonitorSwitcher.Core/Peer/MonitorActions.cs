using MonitorSwitcher.Core.Controls;
using MonitorSwitcher.Core.Ddc;
using MonitorSwitcher.Core.Logging;
using MonitorSwitcher.Core.Storage;
using MonitorSwitcher.Core.Switching;

namespace MonitorSwitcher.Core.Peer;

/// <summary>What a switch or brightness/volume change did, locally or through the paired computer.</summary>
public sealed record ActionResult(
    string MonitorName,
    string? InputId,
    string? InputName,
    MonitorControl? Control,
    int? Percent,
    string? ViaPeer,
    string? TransportId,
    TimeSpan? Elapsed)
{
    /// <summary>"via nvapi in 3 ms" or "via Ashwin's MacBook Air".</summary>
    public string ViaText => ViaPeer is not null
        ? $"via {ViaPeer}"
        : $"via {TransportId ?? "?"}{(Elapsed is { } e ? $" in {e.TotalMilliseconds:0} ms" : "")}";
}

/// <summary>
/// The actions every front end offers (tray app, CLI, hotkeys). Mirrors MonitorActions.swift.
/// </summary>
/// <remarks>
/// Some monitors (the LG 32UN880) only obey the computer they are showing. When this computer is
/// paired with the other one and the monitor has stopped answering here, the request goes to the
/// other computer, which carries it out with its own profiles and safety checks.
/// </remarks>
public sealed class MonitorActions(AppServices services, PeerMachine machine, IPeerChannel? peer)
{
    public AppServices Services => services;

    public PeerMachine Machine => machine;

    public bool IsPaired => peer is not null;

    private SwitchService Switcher => services.Switcher;

    // ---- Acting -------------------------------------------------------------------------------

    public ActionResult SwitchInput(ResolvedMonitor monitor, string inputId, MonitorSettings? settings)
    {
        if (ShouldRelay(monitor))
        {
            return RelaySwitch(monitor, inputId);
        }
        var outcome = Switcher.SwitchTo(monitor, inputId, settings);
        return new ActionResult(monitor.Name, outcome.InputId, outcome.InputName, null, null, null, outcome.Endpoint.Transport.Id, outcome.Elapsed);
    }

    /// <param name="pair">Overrides the toggle inputs from settings (CLI <c>--toggle a,b</c>).</param>
    public ActionResult Toggle(ResolvedMonitor monitor, MonitorSettings? settings, (string A, string B)? pair = null)
    {
        if (ShouldRelay(monitor))
        {
            // The monitor is showing the other computer, so "toggle" means "come back here".
            if (settings?.LocalInputId is not { } local || monitor.Profile.FindInput(local) is null)
            {
                throw new DdcException(
                    DdcErrorKind.Configuration,
                    $"{monitor.Name} is showing another input, and it is not set which input this computer uses.",
                    "Choose the input this computer is plugged into in Settings.");
            }
            return RelaySwitch(monitor, local);
        }

        string target;
        if (pair is { } p)
        {
            services.State.Load().Monitors.TryGetValue(monitor.StableId, out var state);
            target = ToggleLogic.NextInput(p.A, p.B, state?.LastInputId, settings?.LocalInputId,
                monitor.Profile.EffectiveQuirks.IgnoresCommandsWhenInactive);
        }
        else
        {
            target = Switcher.ToggleTarget(monitor, settings);
        }
        return SwitchInput(monitor, target, settings);
    }

    public ActionResult SetControl(ResolvedMonitor monitor, MonitorControl control, ControlChange change)
    {
        if (ShouldRelay(monitor))
        {
            var request = NewRequest(PeerRequestKind.Control, monitor) with
            {
                Control = control.Name(),
                SetPercent = change.IsRelative ? null : change.Value,
                AdjustPercent = change.IsRelative ? change.Value : null,
            };
            var reply = Relay(request);
            return new ActionResult(monitor.Name, null, null, control, reply.Percent, reply.From, null, null);
        }
        var outcome = Switcher.SetControl(monitor, control, change);
        return new ActionResult(monitor.Name, null, null, control, outcome.Percent, null, outcome.Endpoint.Transport.Id, null);
    }

    /// <summary>Checks that the paired computer answers and accepts our pairing code.</summary>
    public PeerReply Ping() => Relay(new PeerRequest { Machine = machine.Id, From = machine.Name, Kind = PeerRequestKind.Ping });

    /// <summary>
    /// Relay only when it is needed and can help: paired, a monitor that ignores inactive
    /// computers, and it does not answer this one right now.
    /// </summary>
    public bool ShouldRelay(ResolvedMonitor monitor) =>
        peer is not null
        && monitor.Profile.EffectiveQuirks.IgnoresCommandsWhenInactive
        && Switcher.IsAnswering(monitor) == false;

    private ActionResult RelaySwitch(ResolvedMonitor monitor, string inputId)
    {
        var input = monitor.Profile.FindInput(inputId) ?? throw new DdcException(
            DdcErrorKind.InputNotInProfile, $"'{inputId}' is not an input of {monitor.Name}.");
        var reply = Relay(NewRequest(PeerRequestKind.Switch, monitor) with { InputId = input.Id });
        try
        {
            services.State.Update(s =>
            {
                var m = s.GetOrAdd(monitor.StableId);
                m.Identity = monitor.Identity;
                m.LastInputId = input.Id;
                m.LastSwitchedAt = DateTimeOffset.Now;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            services.Log.Warn("Could not save state after a switch by the other computer.", ex);
        }
        return new ActionResult(monitor.Name, input.Id, reply.InputName ?? input.Name, null, null, reply.From, null, null);
    }

    private PeerRequest NewRequest(string kind, ResolvedMonitor monitor) =>
        new() { Machine = machine.Id, From = machine.Name, Kind = kind, Monitor = PeerMonitorRef.From(monitor) };

    private PeerReply Relay(PeerRequest request)
    {
        if (peer is null)
        {
            throw new DdcException(DdcErrorKind.PeerUnavailable, "This computer is not paired with another one.",
                "Enter the same pairing code on both computers in Settings.");
        }
        var reply = peer.Send(request);
        if (!reply.Ok)
        {
            throw new DdcException(DdcErrorKind.PeerUnavailable, $"{reply.From} could not do it: {reply.Message}");
        }
        services.Log.Info($"{reply.From} carried out {request.Kind} for this computer: {reply.Message}");
        return reply;
    }

    // ---- Answering the other computer ---------------------------------------------------------

    /// <summary>
    /// Carries out a request from the paired computer. Never relays again, so two computers can't
    /// bounce a request between them.
    /// </summary>
    public PeerReply Handle(PeerRequest request, AppSettings settings)
    {
        PeerReply Fail(string message)
        {
            services.Log.Warn($"Request from {request.From} ({request.Kind}) failed: {message}");
            return new PeerReply { Id = request.Id, From = machine.Name, Ok = false, Message = message };
        }

        if (request.Kind == PeerRequestKind.Ping)
        {
            return new PeerReply { Id = request.Id, From = machine.Name, Ok = true, Message = $"Monitor Switcher on {machine.Name} is ready." };
        }
        if (request.Monitor is not { } reference)
        {
            return Fail("The request did not say which monitor.");
        }

        try
        {
            var monitors = Switcher.DiscoverMonitors(settings);
            if (reference.Find(monitors) is not { } monitor)
            {
                return Fail($"That monitor ({reference.ManufacturerId}, serial {reference.SerialNumber}) is not connected to {machine.Name}.");
            }
            var ms = settings.FindMonitor(monitor.StableId);
            switch (request.Kind)
            {
                case PeerRequestKind.Switch:
                    if (request.InputId is not { } inputId)
                    {
                        return Fail("The request did not say which input.");
                    }
                    var outcome = Switcher.SwitchTo(monitor, inputId, ms);
                    services.Log.Info($"Switched {monitor.Name} to {outcome.InputName} for {request.From}.");
                    return new PeerReply
                    {
                        Id = request.Id, From = machine.Name, Ok = true, Message = $"Switched to {outcome.InputName}.",
                        InputId = outcome.InputId, InputName = outcome.InputName,
                    };
                case PeerRequestKind.Control:
                    if (MonitorControls.Named(request.Control) is not { } control || request.ControlChange is not { } change)
                    {
                        return Fail("The request did not say what to change.");
                    }
                    var result = Switcher.SetControl(monitor, control, change);
                    return new PeerReply
                    {
                        Id = request.Id, From = machine.Name, Ok = true, Message = $"{control.Name()} {result.Percent}%.", Percent = result.Percent,
                    };
                default:
                    return Fail($"Unknown request '{request.Kind}'.");
            }
        }
        catch (DdcException ex)
        {
            return Fail(ex.UserMessage);
        }
    }
}
