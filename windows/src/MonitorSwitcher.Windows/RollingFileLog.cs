using System.Globalization;
using System.Text;
using MonitorSwitcher.Core.Logging;

namespace MonitorSwitcher.Windows;

/// <summary>
/// Appends to logs\monitorswitcher.log and rotates it at 1 MB, keeping 5 files. Safe to use
/// from several processes at once. Logging never throws.
/// </summary>
public sealed class RollingFileLog : ILog
{
    private readonly NamedMutexLock _lock = new(NamedMutexLock.LogFile);
    private readonly string _directory;
    private readonly string _processTag;
    private readonly LogLevel _minimum;
    private readonly long _maxBytes;
    private readonly int _keep;

    public RollingFileLog(string directory, string processTag, LogLevel minimum = LogLevel.Info, long maxBytes = 1_000_000, int keep = 5)
    {
        _directory = directory;
        _processTag = processTag;
        _minimum = minimum;
        _maxBytes = maxBytes;
        _keep = keep;
        FilePath = Path.Combine(directory, "monitorswitcher.log");
    }

    public string FilePath { get; }

    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        if (level < _minimum)
        {
            return;
        }

        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(' ').Append(level.ToString().ToUpperInvariant().PadRight(5))
            .Append(" [").Append(_processTag).Append(':').Append(Environment.ProcessId).Append("] ")
            .Append(message);
        if (exception is not null)
        {
            line.AppendLine().Append("    ").Append(level >= LogLevel.Error ? exception.ToString() : $"{exception.GetType().Name}: {exception.Message}");
        }
        line.AppendLine();

        try
        {
            using (_lock.Acquire(TimeSpan.FromSeconds(2)))
            {
                Directory.CreateDirectory(_directory);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length + line.Length > _maxBytes)
                {
                    Rotate();
                }
                File.AppendAllText(FilePath, line.ToString());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            // Never let logging break a switch.
        }
    }

    private void Rotate()
    {
        var oldest = $"{FilePath}.{_keep - 1}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }
        for (int i = _keep - 2; i >= 1; i--)
        {
            var from = $"{FilePath}.{i}";
            if (File.Exists(from))
            {
                File.Move(from, $"{FilePath}.{i + 1}", overwrite: true);
            }
        }
        File.Move(FilePath, $"{FilePath}.1", overwrite: true);
    }
}
