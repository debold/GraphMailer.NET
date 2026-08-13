using FluentAssertions;
using GraphMailer.Service.Services.Advisor;

namespace GraphMailer.Tests.Unit.Services.Advisor;

/// <summary>
/// Tests for <see cref="RecommendationSummary.OpenByTarget"/>, the aggregation the ConfigTool
/// sidebar uses to badge each navigation entry with a per-page count coloured by the most-severe
/// open hint. Only open suggestions may light up the navigation — done and dismissed ones are
/// nothing to act on — and the colour must follow the worst hint on the page, so a High hint is
/// never hidden behind a Medium count.
/// </summary>
public sealed class RecommendationSummaryOpenByTargetTests
{
    /// <summary>An installation that satisfies every rule — nothing is open.</summary>
    private static RecommendationInput Ideal => new()
    {
        GraphConfigured = true,
        GraphUsesClientSecret = false,
        GraphUsesCertificate = true,
        EnabledListenerCount = 2,
        HasTlsListener = true,
        PlaintextAuthListeners = [],
        SenderValidationEnabled = true,
        BackupEnabled = true,
        NdrEnabled = true,
        UpdateCheckEnabled = true,
        TelemetryEnabled = true,
        LogLevel = "Information",
        HasAdminNotificationRecipients = true,
        AdminNotificationsEnabled = true,
        DisabledCriticalNotifications = [],
        MalwareScanMode = "Enforce",
        MalwareScanProviderPresent = true,
    };

    [Fact]
    public void OpenByTarget_NothingOpen_ReturnsEmpty()
        => RecommendationEngine.Evaluate(Ideal).OpenByTarget().Should().BeEmpty();

    [Fact]
    public void OpenByTarget_CountsOpenHintsPerPage()
    {
        // Three Monitoring rules open at once: update check, log level and telemetry.
        var input = Ideal with { UpdateCheckEnabled = false, LogLevel = "Debug", TelemetryEnabled = false };

        var byTarget = RecommendationEngine.Evaluate(input).OpenByTarget();

        byTarget.Should().ContainKey(RecommendationTarget.Monitoring);
        byTarget[RecommendationTarget.Monitoring].Count.Should().Be(3);
    }

    [Fact]
    public void OpenByTarget_MixedSeverities_KeepsTheHighestPerPage()
    {
        // Notifications carries a Medium hint (NDR off) and a High hint (a critical alert off);
        // the badge must render in the High colour.
        var input = Ideal with
        {
            NdrEnabled = false,
            DisabledCriticalNotifications = ["Email delivery failed"],
        };

        var byTarget = RecommendationEngine.Evaluate(input).OpenByTarget();

        byTarget[RecommendationTarget.Notifications].Count.Should().Be(2);
        byTarget[RecommendationTarget.Notifications].MaxSeverity.Should().Be(RecommendationSeverity.High);
    }

    [Fact]
    public void OpenByTarget_IgnoresDismissedAndDoneHints()
    {
        // Only telemetry is switched off, and it is dismissed — so nothing is open to badge.
        var input = Ideal with { TelemetryEnabled = false };

        var byTarget = RecommendationEngine
            .Evaluate(input, [RecommendationIds.Telemetry])
            .OpenByTarget();

        byTarget.Should().BeEmpty();
    }
}
