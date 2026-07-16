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
}
