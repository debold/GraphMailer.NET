using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GraphMailer.Tests.Unit.Infrastructure.Security;

public sealed class IpBlockingServiceTests
{
    // -------------------------------------------------------------------------
    // Disabled → never blocked, failures never tracked
    // -------------------------------------------------------------------------

    [Fact]
    public void IsBlocked_WhenDisabled_AlwaysReturnsFalse()
    {
        var sut = BuildSut(enabled: false);
        sut.RecordFailure("10.0.0.1", "authFailure");
        sut.IsBlocked("10.0.0.1").Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // Below threshold → not blocked
    // -------------------------------------------------------------------------

    [Fact]
    public void RecordFailure_BelowThreshold_IpNotBlocked()
    {
        var sut = BuildSut(threshold: 3, timeframeSec: 60, blockSec: 600);
        sut.RecordFailure("10.0.0.1", "authFailure");
        sut.RecordFailure("10.0.0.1", "authFailure");

        sut.IsBlocked("10.0.0.1").Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // At threshold → blocked
    // -------------------------------------------------------------------------

    [Fact]
    public void RecordFailure_AtThreshold_IpBlocked()
    {
        var sut = BuildSut(threshold: 3, timeframeSec: 60, blockSec: 600);

        for (var i = 0; i < 3; i++)
            sut.RecordFailure("10.0.0.1", "authFailure");

        sut.IsBlocked("10.0.0.1").Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Different IPs are independent
    // -------------------------------------------------------------------------

    [Fact]
    public void RecordFailure_DifferentIps_AreIndependent()
    {
        var sut = BuildSut(threshold: 2, timeframeSec: 60, blockSec: 600);

        sut.RecordFailure("10.0.0.1", "authFailure");
        sut.RecordFailure("10.0.0.1", "authFailure"); // reaches threshold

        sut.RecordFailure("10.0.0.2", "authFailure"); // only 1 failure

        sut.IsBlocked("10.0.0.1").Should().BeTrue();
        sut.IsBlocked("10.0.0.2").Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // GetBlockedIps snapshot
    // -------------------------------------------------------------------------

    [Fact]
    public void GetBlockedIps_AfterBlock_ContainsIp()
    {
        var sut = BuildSut(threshold: 1, timeframeSec: 60, blockSec: 600);
        sut.RecordFailure("192.168.1.1", "authFailure");

        var blocked = sut.GetBlockedIps();
        blocked.Should().ContainKey("192.168.1.1");
    }

    [Fact]
    public void GetBlockedIps_UnblockedIp_NotIncluded()
    {
        var sut = BuildSut(threshold: 3, timeframeSec: 60, blockSec: 600);
        sut.RecordFailure("192.168.1.1", "authFailure"); // below threshold

        sut.GetBlockedIps().Should().NotContainKey("192.168.1.1");
    }

    // -------------------------------------------------------------------------
    // Multiple failure types
    // -------------------------------------------------------------------------

    [Fact]
    public void RecordFailure_MixedTypes_CountCombined()
    {
        var sut = BuildSut(threshold: 2, timeframeSec: 60, blockSec: 600);

        sut.RecordFailure("10.0.0.5", "authFailure");
        sut.RecordFailure("10.0.0.5", "blockedSender");

        sut.IsBlocked("10.0.0.5").Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Failures outside the time window are not counted (time-based)
    // -------------------------------------------------------------------------

    [Fact]
    public void RecordFailure_FailuresOutsideTimeframe_NotCounted()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sut = BuildSut(threshold: 3, timeframeSec: 60, blockSec: 600, clock: clock);

        // Two failures at t=0
        sut.RecordFailure("10.1.1.1", "authFailure");
        sut.RecordFailure("10.1.1.1", "authFailure");

        // Advance past the timeframe
        clock.Advance(TimeSpan.FromSeconds(61));

        // Third failure at t=61s – only this one is inside the new 60s window
        sut.RecordFailure("10.1.1.1", "authFailure");

        // Total within window: 1, threshold: 3 → not blocked
        sut.IsBlocked("10.1.1.1").Should().BeFalse(
            "the first two failures are outside the 60s window and must not count");
    }

    [Fact]
    public void RecordFailure_FailuresSpanningTwoWindows_OnlyRecentCounted()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sut = BuildSut(threshold: 3, timeframeSec: 60, blockSec: 600, clock: clock);

        // One failure at t=0
        sut.RecordFailure("10.2.2.2", "authFailure");

        // Advance 59s (still inside window)
        clock.Advance(TimeSpan.FromSeconds(59));

        // Two more failures at t=59s → total in window = 3 → blocked
        sut.RecordFailure("10.2.2.2", "authFailure");
        sut.RecordFailure("10.2.2.2", "authFailure");

        sut.IsBlocked("10.2.2.2").Should().BeTrue(
            "all three failures are within the 60s window");
    }

    // -------------------------------------------------------------------------
    // Block expiry (time-based)
    // -------------------------------------------------------------------------

    [Fact]
    public void IsBlocked_AfterBlockExpires_ReturnsFalse()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sut = BuildSut(threshold: 1, timeframeSec: 60, blockSec: 10, clock: clock);

        sut.RecordFailure("10.3.3.3", "authFailure");
        sut.IsBlocked("10.3.3.3").Should().BeTrue("block is active");

        // Advance past the block duration (10 s)
        clock.Advance(TimeSpan.FromSeconds(11));

        sut.IsBlocked("10.3.3.3").Should().BeFalse(
            "block duration of 10s has elapsed; IP should be unblocked");
    }

    [Fact]
    public void IsBlocked_BeforeBlockExpires_ReturnsTrue()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sut = BuildSut(threshold: 1, timeframeSec: 60, blockSec: 30, clock: clock);

        sut.RecordFailure("10.4.4.4", "authFailure");

        clock.Advance(TimeSpan.FromSeconds(29)); // one second before expiry

        sut.IsBlocked("10.4.4.4").Should().BeTrue(
            "block duration of 30s has not elapsed yet");
    }

    [Fact]
    public void GetBlockedIps_AfterBlockExpires_ExcludesExpiredEntry()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sut = BuildSut(threshold: 1, timeframeSec: 60, blockSec: 5, clock: clock);

        sut.RecordFailure("10.5.5.5", "authFailure");
        clock.Advance(TimeSpan.FromSeconds(6));

        // IsBlocked triggers lazy removal
        sut.IsBlocked("10.5.5.5");
        sut.GetBlockedIps().Should().NotContainKey("10.5.5.5",
            "the block has expired and should be removed from the snapshot");
    }

    // -------------------------------------------------------------------------
    // Periodic sweep (global cleanup — lazy per-IP cleanup misses one-off IPs)
    // -------------------------------------------------------------------------

    [Fact]
    public void Sweep_RemovesStaleFailureHistories_OfOneOffIps()
    {
        // Regression: an IP that fails below the threshold once and never returns kept
        // its dictionary entry forever (trivially exploitable via IPv6 rotation).
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sut = BuildSut(threshold: 3, timeframeSec: 60, blockSec: 600, clock: clock);

        sut.RecordFailure("10.9.9.1", "authFailure");
        sut.RecordFailure("10.9.9.2", "authFailure");
        sut.TrackedFailureIpCount.Should().Be(2);

        clock.Advance(TimeSpan.FromSeconds(61));   // both histories fall out of the window
        sut.Sweep();

        sut.TrackedFailureIpCount.Should().Be(0,
            "failure histories outside the tracking window must be evicted globally");
    }

    [Fact]
    public void Sweep_KeepsRecentFailures_AndRemovesExpiredBlocks()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sut = BuildSut(threshold: 1, timeframeSec: 600, blockSec: 10, clock: clock);

        sut.RecordFailure("10.9.9.3", "authFailure");   // blocked immediately (threshold 1)
        clock.Advance(TimeSpan.FromSeconds(11));        // block expired, failure still in window
        sut.Sweep();

        sut.GetBlockedIps().Should().NotContainKey("10.9.9.3", "the expired block is swept");
        sut.TrackedFailureIpCount.Should().Be(1, "the failure itself is still inside the 600s window");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static IpBlockingService BuildSut(
        bool enabled = true,
        int threshold = 10,
        int timeframeSec = 600,
        int blockSec = 600,
        TimeProvider? clock = null)
    {
        var opts = new IpBlockingProtectionOptions
        {
            Enabled = enabled,
            FailureThreshold = threshold,
            TimeframeSeconds = timeframeSec,
            BlockDurationSeconds = blockSec
        };

        return new IpBlockingService(
            new TestOptionsMonitor<IpBlockingProtectionOptions>(opts),
            NullLogger<IpBlockingService>.Instance,
            clock);
    }
}

/// <summary>
/// Minimal controllable TimeProvider for time-based unit tests.
/// </summary>
internal sealed class FakeTimeProvider(DateTimeOffset startTime) : TimeProvider
{
    private DateTimeOffset _now = startTime;

    public void Advance(TimeSpan delta) => _now += delta;

    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>Minimal IOptionsMonitor stub for unit tests.</summary>
internal sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

