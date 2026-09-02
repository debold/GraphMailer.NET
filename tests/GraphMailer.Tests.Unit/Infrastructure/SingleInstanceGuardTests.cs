using GraphMailer.Service.Infrastructure;

namespace GraphMailer.Tests.Unit.Infrastructure;

public sealed class SingleInstanceGuardTests
{
    private static string UniqueName() => "GraphMailer.Tests." + Guid.NewGuid().ToString("N");

    [Fact]
    public void FirstInstance_LockFree_IsPrimary()
    {
        using var guard = new SingleInstanceGuard(UniqueName());

        guard.IsPrimaryInstance.Should().BeTrue();
    }

    [Fact]
    public void SecondInstance_LockHeld_IsNotPrimary()
    {
        var name = UniqueName();
        using var first = new SingleInstanceGuard(name);

        using var second = new SingleInstanceGuard(name);

        second.IsPrimaryInstance.Should().BeFalse();
    }

    [Fact]
    public void NewInstance_AfterPrimaryDisposed_IsPrimary()
    {
        var name = UniqueName();
        var first = new SingleInstanceGuard(name);
        first.Dispose();

        using var second = new SingleInstanceGuard(name);

        second.IsPrimaryInstance.Should().BeTrue();
    }

    [Fact]
    public void Dispose_AsNonPrimary_DoesNotThrow()
    {
        var name = UniqueName();
        using var first = new SingleInstanceGuard(name);
        var second = new SingleInstanceGuard(name);

        var act = () => second.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public void SecondInstance_WithoutAWait_GivesUpImmediately()
    {
        var name = UniqueName();
        using var first = new SingleInstanceGuard(name);

        AcquiresOnAnotherThread(name, waitFor: null).Should().BeFalse();
    }

    /// <summary>
    /// Builds a guard on a thread of its own and reports whether it won the lock.
    ///
    /// A mutex is reentrant for the thread that owns it, so a second guard created on the same
    /// thread would acquire it immediately and prove nothing — the real contenders are separate
    /// processes, and a separate thread is the closest stand-in.
    /// </summary>
    private static bool AcquiresOnAnotherThread(string name, TimeSpan? waitFor)
    {
        var acquired = false;
        var thread = new Thread(() =>
        {
            using var guard = new SingleInstanceGuard(name, waitFor);
            acquired = guard.IsPrimaryInstance;
        });
        thread.Start();
        thread.Join();
        return acquired;
    }

    [Fact]
    public void SecondInstance_WaitExpires_IsNotPrimary()
    {
        var name = UniqueName();
        using var first = new SingleInstanceGuard(name);

        AcquiresOnAnotherThread(name, TimeSpan.FromMilliseconds(200))
            .Should().BeFalse("the holder never let go");
    }

    [Fact]
    public void SecondInstance_HolderReleasesWithinTheWait_BecomesPrimary()
    {
        // The restart case: the outgoing process still holds the lock when the incoming one
        // starts, and releases it a moment later. Without the wait that start is lost.
        //
        // The holder acquires and releases on one thread of its own — a mutex may only be released
        // by the thread that owns it, the way the real owner releases it by exiting.
        var name = UniqueName();
        using var held = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var first = new SingleInstanceGuard(name);
            held.Set();
            Thread.Sleep(300);
        });
        holder.Start();
        held.Wait();

        var acquired = AcquiresOnAnotherThread(name, TimeSpan.FromSeconds(10));
        holder.Join();

        acquired.Should().BeTrue();
    }
}
