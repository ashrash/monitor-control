namespace MonitorSwitcher.Core.Ddc;

/// <summary>What went wrong, so UIs can choose an exit code, icon or wording.</summary>
public enum DdcErrorKind
{
    /// <summary>No transport can reach any monitor (no supported GPU, or every driver API is missing).</summary>
    NoSupportedTransport,
    /// <summary>The GPU vendor's driver library is missing or failed to initialise.</summary>
    DriverMissing,
    MonitorNotFound,
    AmbiguousMonitor,
    /// <summary>The monitor did not acknowledge the command.</summary>
    MonitorNotResponding,
    /// <summary>The monitor is showing another input and ignores this computer until it comes back.</summary>
    MonitorOnOtherInput,
    /// <summary>The requested input is not in the monitor's profile.</summary>
    InputNotInProfile,
    /// <summary>The monitor can only be reached through transports that cannot send this protocol.</summary>
    UnsupportedByTransport,
    /// <summary>A safety rule blocked the write. Always a bug or a bad profile, never a user mistake.</summary>
    SafetyViolation,
    TransportFailure,
    Configuration,
    /// <summary>The paired computer did not answer, or refused the request.</summary>
    PeerUnavailable,
}

public class DdcException : Exception
{
    public DdcException(DdcErrorKind kind, string message, string? hint = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        Hint = hint;
    }

    public DdcErrorKind Kind { get; }

    /// <summary>What the user can do about it, in one sentence. Optional.</summary>
    public string? Hint { get; }

    /// <summary>Message plus hint, ready to show to a person.</summary>
    public string UserMessage => Hint is null ? Message : $"{Message} {Hint}";
}

public sealed class DdcSafetyException(string message)
    : DdcException(DdcErrorKind.SafetyViolation, message, "This is a bug or a bad profile; nothing was sent.");

public static class DdcHints
{
    public const string DdcCiDisabled =
        "Check that DDC/CI is turned on in the monitor's on-screen menu and that the cable carries DDC (most HDMI, DisplayPort and USB-C cables do; some adapters and docks do not).";

    public const string UpdateDriver =
        "Install or update the graphics driver from the GPU vendor's website.";

    public const string OtherInput =
        "Pair this computer with the other one (Settings, Other computer) so it can switch back for you, or use the monitor's buttons.";
}
