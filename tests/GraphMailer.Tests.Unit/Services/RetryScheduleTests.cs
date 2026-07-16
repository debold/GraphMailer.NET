using FluentAssertions;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services;

namespace GraphMailer.Tests.Unit.Services;

public sealed class RetryScheduleTests
{
    private static MailQueueOptions Opts(
        int transientCount = 6, int transientInterval = 300, int steadyInterval = 900, int expirationHours = 24)
        => new()
        {
            TransientRetryCount = transientCount,
            TransientRetryIntervalSeconds = transientInterval,
            RetryIntervalSeconds = steadyInterval,
            MessageExpirationHours = expirationHours,
        };

    [Fact]
    public void NextRetryInterval_WithinTransientPhase_UsesTransientInterval()
    {
        var o = Opts();
        RetrySchedule.NextRetryIntervalSeconds(1, o).Should().Be(300);
        RetrySchedule.NextRetryIntervalSeconds(6, o).Should().Be(300); // last transient retry
    }

    [Fact]
    public void NextRetryInterval_AfterTransientPhase_UsesSteadyInterval()
    {
        var o = Opts();
        RetrySchedule.NextRetryIntervalSeconds(7, o).Should().Be(900);
        RetrySchedule.NextRetryIntervalSeconds(50, o).Should().Be(900);
    }

    [Fact]
    public void HasExpired_BeforeBudget_False()
    {
        var received = new DateTime(2026, 6, 14, 0, 0, 0, DateTimeKind.Utc);
        RetrySchedule.HasExpired(received, received.AddHours(23), expirationHours: 24).Should().BeFalse();
    }

    [Fact]
    public void HasExpired_AtOrAfterBudget_True()
    {
        var received = new DateTime(2026, 6, 14, 0, 0, 0, DateTimeKind.Utc);
        RetrySchedule.HasExpired(received, received.AddHours(24), expirationHours: 24).Should().BeTrue();
        RetrySchedule.HasExpired(received, received.AddHours(30), expirationHours: 24).Should().BeTrue();
    }

    [Fact]
    public void HasExpired_ZeroHours_AlwaysExpired()
    {
        var t = DateTime.UtcNow;
        RetrySchedule.HasExpired(t, t, expirationHours: 0).Should().BeTrue();
    }

    [Fact]
    public void ApproxAttempts_DefaultPolicy_IsAroundHundred()
    {
        // 1 initial + 6 transient + floor((86400-1800)/900)=94 = 101
        RetrySchedule.ApproxAttempts(Opts()).Should().Be(101);
    }

    [Fact]
    public void Describe_DefaultPolicy_IsHumanReadable()
    {
        var text = RetrySchedule.Describe(Opts());

        text.Should().Contain("every 5m");       // transient interval
        text.Should().Contain("first 6 retries");
        text.Should().Contain("then every 15m"); // steady interval
        text.Should().Contain("expires 24h");
        text.Should().Contain("attempts");
    }

    [Fact]
    public void Describe_ExpirationZero_ExplainsImmediateFailure()
    {
        RetrySchedule.Describe(Opts(expirationHours: 0)).Should().Contain("immediately");
    }

    [Fact]
    public void Describe_NoTransientPhase_OmitsTransientWording()
    {
        var text = RetrySchedule.Describe(Opts(transientCount: 0));

        text.Should().Contain("Retry every 15m");
        text.Should().NotContain("first 0 retries");
    }
}
