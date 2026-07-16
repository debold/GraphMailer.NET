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
    public SingleInstanceGuard(string name)
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, $@"Global\{name}", out var createdNew);
            IsPrimaryInstance = createdNew;
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
