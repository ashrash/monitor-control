using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MonitorSwitcher.Core.Logging;

namespace MonitorSwitcher.Core.Storage;

/// <summary>A lock shared by every Monitor Switcher process (tray app and CLI).</summary>
public interface ICrossProcessLock
{
    /// <exception cref="TimeoutException">The lock was not acquired in time.</exception>
    IDisposable Acquire(TimeSpan timeout);
}

/// <summary>In-process lock, for tests and single-process tools.</summary>
public sealed class InProcessLock : ICrossProcessLock
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public IDisposable Acquire(TimeSpan timeout)
    {
        if (!_semaphore.Wait(timeout))
        {
            throw new TimeoutException("Timed out waiting for lock.");
        }
        return new Releaser(_semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}

/// <summary>
/// Loads and saves one JSON file. Writes go to a temp file first and are then moved into place,
/// so a crash never leaves a half-written file. A corrupt file is kept as *.bad and replaced
/// with defaults rather than stopping the app.
/// </summary>
public sealed class JsonFileStore<T>(string path, JsonTypeInfo<T> typeInfo, ICrossProcessLock fileLock, ILog log)
    where T : class, new()
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    public string Path { get; } = path;

    public T Load()
    {
        using (fileLock.Acquire(LockTimeout))
        {
            return LoadUnlocked();
        }
    }

    public void Save(T value)
    {
        using (fileLock.Acquire(LockTimeout))
        {
            SaveUnlocked(value);
        }
    }

    /// <summary>Load, change and save as one step, so concurrent processes don't overwrite each other.</summary>
    public T Update(Action<T> mutate)
    {
        using (fileLock.Acquire(LockTimeout))
        {
            var value = LoadUnlocked();
            mutate(value);
            SaveUnlocked(value);
            return value;
        }
    }

    private T LoadUnlocked()
    {
        if (!File.Exists(Path))
        {
            return new T();
        }

        try
        {
            var json = File.ReadAllText(Path);
            return JsonSerializer.Deserialize(json, typeInfo) ?? new T();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            var backup = Path + ".bad";
            log.Warn($"{Path} could not be read and was reset to defaults; the old file is at {backup}.", ex);
            try
            {
                File.Copy(Path, backup, overwrite: true);
            }
            catch (IOException)
            {
                // Keeping the backup is best effort.
            }
            return new T();
        }
    }

    private void SaveUnlocked(T value)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, typeInfo));
        File.Move(temp, Path, overwrite: true);
    }
}
