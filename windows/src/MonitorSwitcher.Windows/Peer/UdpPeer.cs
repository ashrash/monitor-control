using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using MonitorSwitcher.Core;
using MonitorSwitcher.Core.Ddc;
using MonitorSwitcher.Core.Logging;
using MonitorSwitcher.Core.Peer;
using MonitorSwitcher.Core.Storage;

namespace MonitorSwitcher.Windows.Peer;

/// <summary>
/// UDP transport for requests to the paired computer. Mirrors <c>UdpPeerChannel</c> in PeerUdp.swift.
/// </summary>
/// <remarks>
/// A request goes to the address the peer last answered from and is also broadcast on every local
/// IPv4 network, so it still arrives after the other computer gets a new address. It is resent
/// until a reply arrives or the timeout passes; the receiver answers retries from a cache.
/// </remarks>
public sealed class UdpPeerChannel(PeerKey key, int port, JsonFileStore<AppState> state, ILog log, TimeSpan? timeout = null) : IPeerChannel
{
    private static readonly TimeSpan ResendInterval = TimeSpan.FromMilliseconds(600);
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(4);

    public PeerReply Send(PeerRequest request)
    {
        var datagram = PeerWire.Encode(request, key);
        using var client = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        Network.IgnoreConnectionResets(client);

        var targets = new List<IPAddress>();
        if (state.Load().Peer?.LastAddress is { } last && IPAddress.TryParse(last, out var lastAddress))
        {
            targets.Add(lastAddress);
        }
        foreach (var address in Network.BroadcastAddresses().Append(IPAddress.Broadcast))
        {
            if (!targets.Contains(address))
            {
                targets.Add(address);
            }
        }
        log.Debug($"Sending {request.Kind} to the paired computer via {string.Join(", ", targets)}:{port}.");

        var deadline = DateTime.UtcNow + _timeout;
        while (DateTime.UtcNow < deadline)
        {
            foreach (var target in targets)
            {
                try
                {
                    client.Send(datagram, datagram.Length, new IPEndPoint(target, port));
                }
                catch (SocketException ex)
                {
                    log.Debug($"Sending to {target} failed: {ex.Message}");
                }
            }

            var waitUntil = Min(deadline, DateTime.UtcNow + ResendInterval);
            while (DateTime.UtcNow < waitUntil)
            {
                client.Client.ReceiveTimeout = Math.Max(1, (int)(waitUntil - DateTime.UtcNow).TotalMilliseconds);
                IPEndPoint? sender = null;
                byte[] data;
                try
                {
                    data = client.Receive(ref sender);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    break; // nothing yet: resend
                }
                catch (SocketException ex)
                {
                    log.Debug($"Receive failed: {ex.SocketErrorCode}");
                    continue;
                }
                if (PeerWire.DecodeReply(data, key) is { } reply && reply.Id == request.Id)
                {
                    Remember(sender!.Address.ToString(), reply.From);
                    return reply;
                }
            }
        }
        throw new DdcException(
            DdcErrorKind.PeerUnavailable,
            "The other computer did not answer.",
            $"Check that Monitor Switcher is running there with the same pairing code, that both computers are on the same network, and that the firewall allows Monitor Switcher (UDP port {port}).");
    }

    private void Remember(string address, string name)
    {
        try
        {
            state.Update(s =>
            {
                s.Peer ??= new PeerState();
                s.Peer.LastAddress = address;
                s.Peer.LastName = name;
                s.Peer.LastSeenAt = DateTimeOffset.Now;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            log.Debug($"Could not remember the paired computer's address: {ex.Message}");
        }
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}

/// <summary>
/// Answers requests from the paired computer. Runs one background thread; each request is handled
/// to completion before the next is read, so retries of the same request are answered from cache.
/// </summary>
public sealed class UdpPeerListener(int port, PeerMachine machine, ILog log, Func<PeerKey?> keyProvider, Func<PeerRequest, PeerReply> handler)
    : IDisposable
{
    private readonly PeerReplayGuard _replay = new();
    private UdpClient? _client;
    private Thread? _thread;
    private volatile bool _stopped;

    public void Start()
    {
        try
        {
            _client = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        }
        catch (SocketException ex)
        {
            throw new DdcException(
                DdcErrorKind.PeerUnavailable,
                $"Could not listen on UDP port {port}: {ex.Message}",
                "Another program may be using it; set a different \"port\" under \"peer\" in settings.json on both computers.");
        }
        _client.Client.ReceiveTimeout = 1000;
        Network.IgnoreConnectionResets(_client);
        _thread = new Thread(Run) { IsBackground = true, Name = "MonitorSwitcher peer listener" };
        _thread.Start();
        log.Info($"Listening for the paired computer on UDP port {port}.");
    }

    public void Dispose()
    {
        _stopped = true;
        _client?.Dispose();
    }

    private void Run()
    {
        var client = _client!;
        while (!_stopped)
        {
            IPEndPoint? sender = null;
            byte[] data;
            try
            {
                data = client.Receive(ref sender);
            }
            catch (SocketException)
            {
                continue; // timeout, or an ICMP error from an earlier reply
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                Process(client, data, sender!);
            }
            catch (Exception ex)
            {
                log.Error("Handling a request from the paired computer failed.", ex);
            }
        }
    }

    private void Process(UdpClient client, byte[] data, IPEndPoint sender)
    {
        if (keyProvider() is not { } key || PeerWire.DecodeRequest(data, key) is not { } request || request.Machine == machine.Id)
        {
            return;
        }

        if (_replay.IsDuplicate(request, out var cached))
        {
            if (cached is not null)
            {
                client.Send(cached, cached.Length, sender);
            }
            return;
        }

        PeerReply reply;
        if (_replay.ClockSkew(request) is { } skew)
        {
            reply = new PeerReply
            {
                Id = request.Id,
                From = machine.Name,
                Ok = false,
                Message = $"The clocks of the two computers differ by {Math.Abs(skew)} seconds. Turn on automatic date and time on both.",
            };
            log.Warn($"Refused a request from {request.From} at {sender.Address}: clocks differ by {skew} s.");
        }
        else
        {
            log.Info($"Request from {request.From} at {sender.Address}: {request.Kind}{(request.InputId is null ? "" : $" {request.InputId}")}{(request.Control is null ? "" : $" {request.Control}")}.");
            reply = handler(request);
        }

        var encoded = PeerWire.Encode(reply, key);
        _replay.Remember(request, encoded);
        client.Send(encoded, encoded.Length, sender);
    }
}

/// <summary>Directed broadcast addresses of the computer's IPv4 networks.</summary>
internal static class Network
{
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    /// <summary>
    /// Stops Windows from failing the next receive on a UDP socket with WSAECONNRESET after an
    /// ICMP "port unreachable" (for example from a computer that doesn't run Monitor Switcher).
    /// </summary>
    public static void IgnoreConnectionResets(UdpClient client)
    {
        try
        {
            client.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException or NotSupportedException)
        {
            // Not Windows, or not supported: receive errors are handled anyway.
        }
    }

    public static IReadOnlyList<IPAddress> BroadcastAddresses()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                    {
                        continue;
                    }
                    var address = unicast.Address.GetAddressBytes();
                    var mask = unicast.IPv4Mask.GetAddressBytes();
                    var broadcast = new byte[4];
                    for (int i = 0; i < 4; i++)
                    {
                        broadcast[i] = (byte)(address[i] | ~mask[i]);
                    }
                    var ip = new IPAddress(broadcast);
                    if (!result.Contains(ip))
                    {
                        result.Add(ip);
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Fall back to the limited broadcast address only.
        }
        return result;
    }
}

/// <summary>Builds <see cref="MonitorActions"/> with relaying to the paired computer when paired.</summary>
public static class PeerFactory
{
    public static string ComputerName() => Environment.MachineName;

    public static MonitorActions Actions(AppServices services, AppSettings settings)
    {
        var machine = PeerMachine.Load(services.State, ComputerName());
        IPeerChannel? channel = settings.Peer?.Key is { } key
            ? new UdpPeerChannel(key, settings.Peer.EffectivePort, services.State, services.Log)
            : null;
        return new MonitorActions(services, machine, channel);
    }
}
