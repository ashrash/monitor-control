using MonitorSwitcher.Core.Ddc;
using MonitorSwitcher.Core.Monitors;

namespace MonitorSwitcher.Core.Protocols;

/// <summary>All known protocols. Add new vendor protocols to <see cref="All"/>.</summary>
public static class ProtocolRegistry
{
    private static readonly InputProtocol[] Protocols =
    [
        new StandardMccsProtocol(),
        new LgAlternativeProtocol(),
    ];

    public static IReadOnlyList<InputProtocol> All => Protocols;

    public static InputProtocol? Find(string? id) =>
        Protocols.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public static InputProtocol Get(string id) =>
        Find(id) ?? throw new DdcException(
            DdcErrorKind.Configuration,
            $"Unknown protocol '{id}'. Known protocols: {string.Join(", ", Protocols.Select(p => p.Id))}.");

    /// <summary>Protocols worth trying on an unknown monitor, most promising first.</summary>
    public static IReadOnlyList<InputProtocol> RankedFor(MonitorIdentity monitor, MonitorCapabilities? capabilities) =>
        Protocols
            .Select(p => (Protocol: p, Rank: p.RankFor(monitor, capabilities)))
            .Where(x => x.Rank > 0)
            .OrderByDescending(x => x.Rank)
            .Select(x => x.Protocol)
            .ToArray();
}
