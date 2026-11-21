using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MonitorSwitcher.Core.Controls;
using MonitorSwitcher.Core.Monitors;
using MonitorSwitcher.Core.Storage;
using MonitorSwitcher.Core.Switching;

namespace MonitorSwitcher.Core.Peer;

// Messages between the two computers that share a monitor. Mirrors PeerProtocol.swift; both sides
// must produce and accept exactly the same bytes (see spec/peer-protocol.md and spec/peer-vectors.json).
//
// A datagram is UTF-8 text: "MSW1 <64 hex HMAC> <JSON>". The HMAC-SHA256 covers
// "MSW1 request\n" or "MSW1 reply\n" followed by the JSON bytes exactly as sent, so neither side
// has to re-serialize JSON identically, and a reply can never be replayed as a request.
//
// Requests only name a monitor and an input id or a brightness/volume change. The receiving
// computer checks them against its own profiles, exactly as if its own user had asked, so a peer
// can never make it send bytes its profiles don't allow.

/// <summary>
/// The shared pairing code, typed on both computers. 20 characters from a 31-letter alphabet
/// without look-alikes (no I, L, O, 0, 1), which is about 99 bits.
/// </summary>
public static class PairingCode
{
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int Length = 20;
    public const int MinimumLength = 16;

    public static string Generate()
    {
        var chars = new char[Length];
        for (int i = 0; i < Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }
        return Format(new string(chars));
    }

    /// <summary>Groups of four: "K7QF-2M9X-HC4T-8WPA-R3NB".</summary>
    public static string Format(string normalized) =>
        string.Join('-', normalized.Chunk(4).Select(c => new string(c)));

    /// <summary>Upper case without spaces and dashes, or null if it is too short or has other characters.</summary>
    public static string? Normalize(string? text)
    {
        var cleaned = new string((text ?? "").ToUpperInvariant().Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        return cleaned.Length is >= MinimumLength and <= 64 && cleaned.All(c => Alphabet.Contains(c)) ? cleaned : null;
    }
}

/// <summary>The secret both computers derive from the pairing code.</summary>
public sealed class PeerKey : IEquatable<PeerKey>
{
    private PeerKey(byte[] bytes) => Bytes = bytes;

    public byte[] Bytes { get; }

    public static PeerKey? FromPairingCode(string pairingCode) =>
        PairingCode.Normalize(pairingCode) is { } normalized
            ? new PeerKey(SHA256.HashData(Encoding.UTF8.GetBytes($"MonitorSwitcher pairing v1\n{normalized}")))
            : null;

    internal byte[] Mac(string label, ReadOnlySpan<byte> json)
    {
        var prefix = Encoding.UTF8.GetBytes($"MSW1 {label}\n");
        var message = new byte[prefix.Length + json.Length];
        prefix.CopyTo(message, 0);
        json.CopyTo(message.AsSpan(prefix.Length));
        return HMACSHA256.HashData(Bytes, message);
    }

    public bool Equals(PeerKey? other) => other is not null && Bytes.AsSpan().SequenceEqual(other.Bytes);

    public override bool Equals(object? obj) => Equals(obj as PeerKey);

    public override int GetHashCode() => BitConverter.ToInt32(Bytes, 0);
}

public static class PeerRequestKind
{
    public const string Ping = "ping";
    public const string Switch = "switch";
    public const string Control = "control";
}

/// <summary>
/// Which monitor a request is about. The two computers may see different product codes (the LG
/// 32UN880 reports 0x774F over HDMI and 0x7750 over USB-C), so the serial number decides.
/// </summary>
public sealed record PeerMonitorRef
{
    public string ManufacturerId { get; init; } = "";

    public ushort ProductCode { get; init; }

    public uint SerialNumber { get; init; }

    public string? SerialString { get; init; }

    /// <summary>Profile id when a profile matched; used only when serials are missing.</summary>
    public string? ProfileId { get; init; }

    public static PeerMonitorRef From(ResolvedMonitor monitor) => new()
    {
        ManufacturerId = monitor.Identity.ManufacturerId,
        ProductCode = monitor.Identity.ProductCode,
        SerialNumber = monitor.Identity.SerialNumber,
        SerialString = monitor.Identity.SerialString,
        ProfileId = monitor.NeedsSetup ? null : monitor.Profile.Id,
    };

    /// <summary>Same maker and same serial number (or EDID serial text).</summary>
    public bool SameSerial(MonitorIdentity identity)
    {
        if (!string.Equals(ManufacturerId, identity.ManufacturerId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (SerialNumber != 0 && SerialNumber == identity.SerialNumber)
        {
            return true;
        }
        var a = SerialString?.Trim() ?? "";
        return a.Length > 0 && string.Equals(a, identity.SerialString?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The local monitor this reference means, or null when none or several could be meant.</summary>
    public ResolvedMonitor? Find(IReadOnlyList<ResolvedMonitor> monitors)
    {
        var bySerial = monitors.Where(m => SameSerial(m.Identity)).ToList();
        if (bySerial.Count == 1)
        {
            return bySerial[0];
        }
        if (bySerial.Count > 1)
        {
            var exact = bySerial.Where(m => m.Identity.ProductCode == ProductCode).ToList();
            return exact.Count == 1 ? exact[0] : null;
        }
        if (ProfileId is null)
        {
            return null;
        }
        var byProfile = monitors.Where(m => !m.NeedsSetup && string.Equals(m.Profile.Id, ProfileId, StringComparison.OrdinalIgnoreCase)).ToList();
        return byProfile.Count == 1 ? byProfile[0] : null;
    }
}

public sealed record PeerRequest
{
    public int V { get; init; } = PeerWire.Version;

    /// <summary>Random per request; the receiver uses it to drop retries it has already handled.</summary>
    public string Id { get; init; } = PeerWire.NewId();

    /// <summary>Unix time in seconds; requests more than <see cref="PeerWire.MaxClockSkew"/> away are refused.</summary>
    public long Ts { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Sender's machine id, so a computer ignores its own broadcasts.</summary>
    public string Machine { get; init; } = "";

    /// <summary>Sender's computer name, for messages.</summary>
    public string From { get; init; } = "";

    /// <summary>One of <see cref="PeerRequestKind"/>.</summary>
    public string Kind { get; init; } = "";

    public PeerMonitorRef? Monitor { get; init; }

    public string? InputId { get; init; }

    public string? Control { get; init; }

    public int? SetPercent { get; init; }

    public int? AdjustPercent { get; init; }

    [JsonIgnore]
    public ControlChange? ControlChange =>
        SetPercent is { } set ? Controls.ControlChange.Set(set)
        : AdjustPercent is { } delta ? Controls.ControlChange.Adjust(delta)
        : null;
}

public sealed record PeerReply
{
    public int V { get; init; } = PeerWire.Version;

    /// <summary>The request's id.</summary>
    public string Id { get; init; } = "";

    public string From { get; init; } = "";

    public bool Ok { get; init; }

    public string Message { get; init; } = "";

    public string? InputId { get; init; }

    public string? InputName { get; init; }

    public int? Percent { get; init; }
}

/// <summary>Compact JSON for datagrams (property names match the Swift side).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(PeerRequest))]
[JsonSerializable(typeof(PeerReply))]
public partial class PeerJsonContext : JsonSerializerContext;

public static class PeerWire
{
    public const int Version = 1;
    public const long MaxClockSkew = 120;
    public const int MaxDatagram = 8192;
    private const string Prefix = "MSW1 ";
    private const int HeaderLength = 5 + 64 + 1;

    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public static byte[] Encode(PeerRequest request, PeerKey key) =>
        Encode(JsonSerializer.SerializeToUtf8Bytes(request, PeerJsonContext.Default.PeerRequest), "request", key);

    public static byte[] Encode(PeerReply reply, PeerKey key) =>
        Encode(JsonSerializer.SerializeToUtf8Bytes(reply, PeerJsonContext.Default.PeerReply), "reply", key);

    public static PeerRequest? DecodeRequest(ReadOnlySpan<byte> data, PeerKey key) =>
        Verify(data, "request", key) is { } json ? Deserialize(json, PeerJsonContext.Default.PeerRequest) : null;

    public static PeerReply? DecodeReply(ReadOnlySpan<byte> data, PeerKey key) =>
        Verify(data, "reply", key) is { } json ? Deserialize(json, PeerJsonContext.Default.PeerReply) : null;

    private static byte[] Encode(byte[] json, string label, PeerKey key)
    {
        var header = Encoding.ASCII.GetBytes(Prefix + Convert.ToHexStringLower(key.Mac(label, json)) + " ");
        return [.. header, .. json];
    }

    /// <summary>The JSON bytes when the header and HMAC are right, else null.</summary>
    private static byte[]? Verify(ReadOnlySpan<byte> data, string label, PeerKey key)
    {
        if (data.Length <= HeaderLength || data.Length > MaxDatagram
            || !data.StartsWith("MSW1 "u8) || data[HeaderLength - 1] != (byte)' ')
        {
            return null;
        }
        byte[] mac;
        try
        {
            mac = Convert.FromHexString(Encoding.ASCII.GetString(data[Prefix.Length..(HeaderLength - 1)]));
        }
        catch (FormatException)
        {
            return null;
        }
        var json = data[HeaderLength..];
        return CryptographicOperations.FixedTimeEquals(mac, key.Mac(label, json)) ? json.ToArray() : null;
    }

    private static T? Deserialize<T>(byte[] json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Receiver-side bookkeeping: refuses stale requests and answers retries from a cache instead of
/// acting twice. Not thread-safe; the listener uses it from one thread.
/// </summary>
public sealed class PeerReplayGuard
{
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, (DateTimeOffset At, byte[]? Reply)> _seen = [];

    /// <summary>Seconds between the request's clock and ours when that exceeds <see cref="PeerWire.MaxClockSkew"/>, else null.</summary>
    public long? ClockSkew(PeerRequest request, DateTimeOffset? now = null)
    {
        var skew = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() - request.Ts;
        return Math.Abs(skew) > PeerWire.MaxClockSkew ? skew : null;
    }

    /// <summary>True when this request id was handled before; <paramref name="cached"/> is the reply sent then, if any.</summary>
    public bool IsDuplicate(PeerRequest request, out byte[]? cached)
    {
        if (_seen.TryGetValue(request.Id, out var entry))
        {
            cached = entry.Reply;
            return true;
        }
        cached = null;
        return false;
    }

    public void Remember(PeerRequest request, byte[]? reply, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        foreach (var old in _seen.Where(kv => at - kv.Value.At >= Keep).Select(kv => kv.Key).ToList())
        {
            _seen.Remove(old);
        }
        _seen[request.Id] = (at, reply);
    }
}

/// <summary>Sends a request to the paired computer and waits for its reply. Implemented per platform (UDP).</summary>
public interface IPeerChannel
{
    PeerReply Send(PeerRequest request);
}

/// <summary>This computer, as named in requests.</summary>
public sealed record PeerMachine(string Id, string Name)
{
    /// <summary>Reads this computer's machine id from state.json, creating it on first use.</summary>
    public static PeerMachine Load(JsonFileStore<AppState> state, string name)
    {
        if (state.Load().MachineId is { Length: > 0 } id)
        {
            return new PeerMachine(id, name);
        }
        try
        {
            var updated = state.Update(s =>
            {
                if (string.IsNullOrEmpty(s.MachineId))
                {
                    s.MachineId = PeerWire.NewId();
                }
            });
            return new PeerMachine(updated.MachineId ?? PeerWire.NewId(), name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            return new PeerMachine(PeerWire.NewId(), name);
        }
    }
}
