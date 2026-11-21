namespace MonitorSwitcher.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>Minimal logging abstraction; the Windows implementation writes a rotating file.</summary>
public interface ILog
{
    void Write(LogLevel level, string message, Exception? exception = null);
}

public static class LogExtensions
{
    public static void Debug(this ILog log, string message) => log.Write(LogLevel.Debug, message);

    public static void Info(this ILog log, string message) => log.Write(LogLevel.Info, message);

    public static void Warn(this ILog log, string message, Exception? exception = null) =>
        log.Write(LogLevel.Warn, message, exception);

    public static void Error(this ILog log, string message, Exception? exception = null) =>
        log.Write(LogLevel.Error, message, exception);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();

    public void Write(LogLevel level, string message, Exception? exception = null)
    {
    }
}

/// <summary>Writes to several logs.</summary>
public sealed class TeeLog(params ILog[] logs) : ILog
{
    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        foreach (var log in logs)
        {
            log.Write(level, message, exception);
        }
    }
}

/// <summary>Collects log lines in memory. Used by tests and by <c>--diagnose</c>.</summary>
public sealed class MemoryLog : ILog
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return _lines.ToArray();
            }
        }
    }

    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        lock (_lines)
        {
            _lines.Add(exception is null ? $"{level}: {message}" : $"{level}: {message} ({exception.Message})");
        }
    }
}
