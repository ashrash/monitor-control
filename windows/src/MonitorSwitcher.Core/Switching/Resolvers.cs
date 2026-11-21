using MonitorSwitcher.Core.Ddc;
using MonitorSwitcher.Core.Profiles;
using MonitorSwitcher.Core.Storage;

namespace MonitorSwitcher.Core.Switching;

/// <summary>Turns what a person types ("usbc", "USB-C", "Mac", "us") into a profile input.</summary>
public static class InputResolver
{
    public static ProfileInput Resolve(MonitorProfile profile, MonitorSettings? settings, string query)
    {
        var q = query.Trim();
        var inputs = profile.Inputs;

        var exact = inputs.FirstOrDefault(i => Eq(i.Id, q))
            ?? inputs.FirstOrDefault(i => Eq(i.Name, q))
            ?? inputs.FirstOrDefault(i => Eq(settings?.LabelFor(i.Id), q));
        if (exact is not null)
        {
            return exact;
        }

        var prefix = inputs
            .Where(i => StartsWith(i.Id, q) || StartsWith(i.Name, q) || StartsWith(settings?.LabelFor(i.Id), q))
            .ToList();
        if (prefix.Count == 1)
        {
            return prefix[0];
        }

        throw new DdcException(
            DdcErrorKind.InputNotInProfile,
            prefix.Count > 1
                ? $"'{query}' matches more than one input of {profile.Name}: {Describe(prefix, settings)}."
                : $"'{query}' is not an input of {profile.Name}. Choose one of: {Describe(inputs, settings)}.");
    }

    public static string Describe(IEnumerable<ProfileInput> inputs, MonitorSettings? settings) =>
        string.Join(", ", inputs.Select(i => settings?.LabelFor(i.Id) is { } label
            ? $"{i.Id} ({i.Name}, \"{label}\")"
            : $"{i.Id} ({i.Name})"));

    private static bool Eq(string? a, string b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool StartsWith(string? a, string b) => a is not null && a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Picks exactly one monitor. Never broadcasts: ambiguity is an error.</summary>
public static class MonitorResolver
{
    public static ResolvedMonitor Resolve(IReadOnlyList<ResolvedMonitor> monitors, AppSettings settings, string? query)
    {
        if (monitors.Count == 0)
        {
            throw new DdcException(
                DdcErrorKind.MonitorNotFound,
                "No monitor that supports DDC/CI was found.",
                DdcHints.DdcCiDisabled);
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            if (settings.DefaultMonitorId is { } defaultId && monitors.FirstOrDefault(m => m.StableId == defaultId) is { } preferred)
            {
                return preferred;
            }
            if (monitors.Count == 1)
            {
                return monitors[0];
            }
            throw Ambiguous(monitors, settings, "Several monitors are connected.");
        }

        var q = query.Trim();
        if (int.TryParse(q, out var index) && index >= 1 && index <= monitors.Count)
        {
            return monitors[index - 1];
        }

        var exact = monitors.Where(m =>
            Eq(m.StableId, q)
            || Eq(m.OsDisplayName, q)
            || Eq(m.OsDisplayName?.TrimStart('\\', '.'), q)
            || Eq(settings.FindMonitor(m.StableId)?.Label, q)
            || Eq(m.Name, q)
            || Eq(m.Identity.ModelName, q)).ToList();
        if (exact.Count == 1)
        {
            return exact[0];
        }

        var partial = monitors.Where(m =>
            Contains(m.Name, q)
            || Contains(m.Identity.ModelName, q)
            || Contains(settings.FindMonitor(m.StableId)?.Label, q)).ToList();
        if (exact.Count == 0 && partial.Count == 1)
        {
            return partial[0];
        }

        throw exact.Count > 1 || partial.Count > 1
            ? Ambiguous(monitors, settings, $"'{query}' matches more than one monitor.")
            : new DdcException(DdcErrorKind.MonitorNotFound, $"No connected monitor matches '{query}'. Connected: {List(monitors, settings)}.");
    }

    private static DdcException Ambiguous(IReadOnlyList<ResolvedMonitor> monitors, AppSettings settings, string reason) =>
        new(DdcErrorKind.AmbiguousMonitor,
            $"{reason} Choose one with --monitor <number or name>: {List(monitors, settings)}.",
            "Or pick a default monitor in Settings.");

    private static string List(IReadOnlyList<ResolvedMonitor> monitors, AppSettings settings) =>
        string.Join("; ", monitors.Select((m, i) => $"{i + 1}) {settings.FindMonitor(m.StableId)?.Label ?? m.Name} [{m.StableId}]"));

    private static bool Eq(string? a, string b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string? a, string b) => a is not null && a.Contains(b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Chooses the toggle target. The monitor can't report its input, so we use what we last sent.</summary>
public static class ToggleLogic
{
    /// <summary>The two toggle inputs: from settings, else the profile's two verified inputs, else a two-input profile.</summary>
    public static (string A, string B)? ToggleInputs(MonitorProfile profile, MonitorSettings? settings)
    {
        if (settings?.ToggleInputs is [var a, var b]
            && profile.FindInput(a) is not null
            && profile.FindInput(b) is not null
            && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return (a, b);
        }

        var verified = profile.Inputs.Where(i => i.Verified).ToList();
        if (verified.Count == 2)
        {
            return (verified[0].Id, verified[1].Id);
        }
        if (profile.Inputs.Count == 2)
        {
            return (profile.Inputs[0].Id, profile.Inputs[1].Id);
        }
        return null;
    }

    /// <summary>
    /// Last sent was A → B, and vice versa. With no history, assume the monitor shows this computer
    /// and go to the other input; if even that is unknown, go to B.
    /// </summary>
    /// <param name="ignoresCommandsWhenInactive">
    /// The monitor only obeys the computer it is showing. Then any command this computer can
    /// deliver must be "go elsewhere", so the target is always the input that isn't this computer's.
    /// </param>
    public static string NextInput(string a, string b, string? lastInputId, string? localInputId, bool ignoresCommandsWhenInactive = false)
    {
        if (ignoresCommandsWhenInactive && Eq(localInputId, a))
        {
            return b;
        }
        if (ignoresCommandsWhenInactive && Eq(localInputId, b))
        {
            return a;
        }
        if (Eq(lastInputId, a))
        {
            return b;
        }
        if (Eq(lastInputId, b))
        {
            return a;
        }
        if (Eq(localInputId, b))
        {
            return a;
        }
        return b;
    }

    private static bool Eq(string? x, string y) => x is not null && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
}
