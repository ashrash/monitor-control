using MonitorSwitcher.Core.Storage;

namespace MonitorSwitcher.Windows;

/// <summary>
/// Cross-process lock backed by a named mutex in the user's session, shared by the tray app and
/// the CLI. Must be released on the thread that acquired it.
/// </summary>
public sealed class NamedMutexLock(string name) : ICrossProcessLock
{
    public const string DdcBus = @"Local\MonitorSwitcher.DdcBus";
    public const string StateFile = @"Local\MonitorSwitcher.State";
    public const string SettingsFile = @"Local\MonitorSwitcher.Settings";
    public const string LogFile = @"Local\MonitorSwitcher.Log";

    public IDisposable Acquire(TimeSpan timeout)
    {
        var mutex = new Mutex(initiallyOwned: false, name);
        bool owned;
        try
        {
            owned = mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // the previous owner crashed; the protected resource is still usable
        }

        if (!owned)
        {
            mutex.Dispose();
            throw new TimeoutException($"Another Monitor Switcher process is holding '{name}'.");
        }
        return new Releaser(mutex);
    }

    private sealed class Releaser(Mutex mutex) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                mutex.ReleaseMutex();
                mutex.Dispose();
            }
        }
    }
}
