using FluentAssertions;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GraphMailer.Tests.Unit.Services;

/// <summary>
/// The single place that decides how often a lasting problem is reported. Every state-based
/// monitor routes through it, so these tests pin the cadence that used to differ per monitor:
/// some mailed on every check, others exactly once per process lifetime.
/// </summary>
public sealed class AlertStateStoreTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "alertstate-tests-" + Guid.NewGuid().ToString("N"));

    private string StatePath => Path.Combine(_dir, "alert-state.json");

    private AlertStateStore Create(TimeProvider? time = null)
        => new(StatePath, NullLogger<AlertStateStore>.Instance, time);

    /// <summary>Minimal manual clock — enough to step past a repeat interval without waiting.</summary>
    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void ShouldNotify_FirstRaise_ReturnsTrue()
    {
        var sut = Create();

        sut.ShouldNotify("disk-low", "C:\\", 1440).Should().BeTrue();
    }

    [Fact]
    public void ShouldNotify_SameConditionWithinInterval_ReturnsFalse()
    {
        var sut = Create();
        sut.ShouldNotify("disk-low", "C:\\", 1440);

        sut.ShouldNotify("disk-low", "C:\\", 1440).Should().BeFalse();
    }

    [Fact]
    public void ShouldNotify_RenotifyIntervalElapsed_ReturnsTrue()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 7, 27, 8, 0, 0, TimeSpan.Zero));
        var sut = Create(clock);
        sut.ShouldNotify("disk-low", "C:\\", 1440);

        clock.Advance(TimeSpan.FromMinutes(1439));
        sut.ShouldNotify("disk-low", "C:\\", 1440).Should().BeFalse();

        clock.Advance(TimeSpan.FromMinutes(1));
        sut.ShouldNotify("disk-low", "C:\\", 1440).Should().BeTrue();
    }

    [Fact]
    public void ShouldNotify_RenotifyDisabled_NeverRepeats()
    {
        var sut = Create();
        sut.ShouldNotify("disk-low", "C:\\", renotifyMinutes: 0).Should().BeTrue();

        sut.ShouldNotify("disk-low", "C:\\", renotifyMinutes: 0).Should().BeFalse();
        sut.ShouldNotify("disk-low", "C:\\", renotifyMinutes: 0).Should().BeFalse();
    }

    [Fact]
    public void ShouldNotify_DetailChanged_ReportsImmediately()
    {
        var sut = Create();
        sut.ShouldNotify("cert-tls", "expiring:CN=relay", 1440);

        // Escalation must not wait out the repeat interval.
        sut.ShouldNotify("cert-tls", "expired:CN=relay", 1440).Should().BeTrue();
    }

    [Fact]
    public void Clear_ActiveAlert_ReturnsTrueOnceThenFalse()
    {
        var sut = Create();
        sut.ShouldNotify("graph-api", "unreachable", 1440);

        sut.Clear("graph-api").Should().BeTrue();
        sut.Clear("graph-api").Should().BeFalse();
    }

    [Fact]
    public void Clear_NeverRaised_ReturnsFalse()
    {
        var sut = Create();

        sut.Clear("port-2525").Should().BeFalse();
    }

    [Fact]
    public void Clear_ThenRaiseAgain_ReportsImmediately()
    {
        var sut = Create();
        sut.ShouldNotify("port-2525", "down", 1440);
        sut.Clear("port-2525");

        sut.ShouldNotify("port-2525", "down", 1440).Should().BeTrue();
    }

    [Fact]
    public void ShouldNotify_SurvivesRestart_DoesNotRepeatAfterReload()
    {
        Create().ShouldNotify("disk-low", "C:\\", 1440).Should().BeTrue();

        // A new instance stands in for a service restart: the ongoing condition must not re-alert.
        Create().ShouldNotify("disk-low", "C:\\", 1440).Should().BeFalse();
    }

    [Fact]
    public void Clear_SurvivesRestart_StillSendsRecovery()
    {
        Create().ShouldNotify("graph-api", "unreachable", 1440);

        // The outage started before the restart — the recovery mail is still owed.
        Create().Clear("graph-api").Should().BeTrue();
    }

    [Fact]
    public void ShouldNotify_KeysAreIndependent()
    {
        var sut = Create();
        sut.ShouldNotify("port-25", "down", 1440).Should().BeTrue();

        sut.ShouldNotify("port-587", "down", 1440).Should().BeTrue();
        sut.Clear("port-25").Should().BeTrue();
        sut.Clear("port-587").Should().BeTrue();
    }

    [Fact]
    public void Load_CorruptStateFile_StartsEmptyInsteadOfThrowing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, "{ this is not json");

        var sut = Create();

        // A lost state file costs at most one redundant notification; failing the check would
        // take the whole monitor down instead.
        sut.ShouldNotify("disk-low", "C:\\", 1440).Should().BeTrue();
    }
}
