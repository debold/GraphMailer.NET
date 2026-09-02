namespace GraphMailer.Service.Infrastructure;

/// <summary>
/// Machine-wide single-instance lock based on a named kernel mutex in the
/// <c>Global\</c> namespace (covers all sessions, e.g. parallel RDP logons).
///
/// The mutex is held for the lifetime of this object; the OS releases it
/// automatically when the owning process exits, so a crashed instance never
/// blocks a restart.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex? _mutex;

    /// <summary>True when this process acquired the lock and may run.</summary>
    public bool IsPrimaryInstance { get; }

    /// <param name="name">
    /// Application-unique lock name, e.g. <c>"GraphMailer.Service"</c>.
    /// Prefixed with <c>Global\</c> internally.
    /// </param>
    /// <param name="waitFor">
    /// How long to wait for the lock when another process still holds it. Null gives up at once.
    ///
    /// A restart needs this: Windows reports the service as stopped as soon as it says it has shut
    /// down, but the process lives on for a moment afterwards — long enough to finish the "service
    /// stopping" admin mail — and only releases the lock when it exits. Without a grace period the
    /// incoming instance loses that race and exits before it can even reach the service control
    /// manager, which then waits for a connection that never comes.
    /// </param>
    public SingleInstanceGuard(string name, TimeSpan? waitFor = null)
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, $@"Global\{name}", out var createdNew);
            if (createdNew)
            {
                IsPrimaryInstance = true;
                return;
            }

            IsPrimaryInstance = waitFor is { } timeout && _mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died without releasing it; waiting handed ownership to us.
            IsPrimaryInstance = true;
        }
        catch (UnauthorizedAccessException)
        {
            // Mutex exists but belongs to another account (e.g. the service
            // running as LocalSystem) – definitely another instance.
            IsPrimaryInstance = false;
        }
    }

    public void Dispose()
    {
        if (_mutex is null)
            return;

        if (IsPrimaryInstance)
            _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
