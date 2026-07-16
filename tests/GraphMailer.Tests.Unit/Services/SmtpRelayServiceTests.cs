using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging;

namespace GraphMailer.Tests.Unit.Services;

/// <summary>
/// Listener selection: disabled <see cref="SmtpServerEntry"/> entries are not started,
/// and entries that can never start (invalid or duplicate port) are skipped with an
/// error log instead of crashing the whole service.
/// </summary>
public sealed class SmtpRelayServiceTests
{
    [Fact]
    public void SelectActiveListeners_ExcludesDisabledEntries()
    {
        var entries = new List<SmtpServerEntry>
        {
            new() { Port = 25,  Enabled = true },
            new() { Port = 465, Enabled = false },
            new() { Port = 587, Enabled = true },
        };

        var active = SmtpRelayService.SelectActiveListeners(entries);

        active.Select(e => e.Port).Should().Equal(25, 587);
    }

    [Fact]
    public void SelectActiveListeners_AllEnabled_ReturnsAll()
    {
        var entries = new List<SmtpServerEntry> { new() { Port = 25 }, new() { Port = 587 } };

        SmtpRelayService.SelectActiveListeners(entries).Should().HaveCount(2);
    }

    [Fact]
    public void SelectActiveListeners_AllDisabled_ReturnsEmpty()
    {
        var entries = new List<SmtpServerEntry> { new() { Port = 25, Enabled = false } };

        SmtpRelayService.SelectActiveListeners(entries).Should().BeEmpty();
    }

    // =========================================================================
    // Startable-listener validation (a bad config value must not crash the service)
    // =========================================================================

    [Fact]
    public void SelectStartableListeners_InvalidPort_IsSkippedAndLoggedAsError()
    {
        // Regression: an out-of-range port used to throw during server construction and
        // take the whole Windows service down. The log is the operator's only signal
        // that a configured listener is missing.
        var logger = new FakeLogger<SmtpRelayService>();
        var entries = new List<SmtpServerEntry>
        {
            new() { Name = "Bad-Zero",  Port = 0 },
            new() { Name = "Good",      Port = 25 },
            new() { Name = "Bad-Range", Port = 70000 },
        };

        var startable = SmtpRelayService.SelectStartableListeners(entries, logger);

        startable.Select(e => e.Port).Should().Equal(25);
        logger.HasEntry(LogLevel.Error, "invalid port 0").Should().BeTrue();
        logger.HasEntry(LogLevel.Error, "invalid port 70000").Should().BeTrue();
    }

    [Fact]
    public void SelectStartableListeners_DuplicatePort_FirstEntryWinsSecondIsSkipped()
    {
        var logger = new FakeLogger<SmtpRelayService>();
        var entries = new List<SmtpServerEntry>
        {
            new() { Name = "First",  Port = 587 },
            new() { Name = "Second", Port = 587 },
        };

        var startable = SmtpRelayService.SelectStartableListeners(entries, logger);

        startable.Should().ContainSingle().Which.Name.Should().Be("First");
        logger.HasEntry(LogLevel.Error, "already assigned").Should().BeTrue();
    }

    [Fact]
    public void SelectStartableListeners_AllValid_ReturnsAllWithoutLogging()
    {
        var logger = new FakeLogger<SmtpRelayService>();
        var entries = new List<SmtpServerEntry>
        {
            new() { Port = 25 }, new() { Port = 465 }, new() { Port = 587 },
        };

        var startable = SmtpRelayService.SelectStartableListeners(entries, logger);

        startable.Should().HaveCount(3);
        logger.Entries.Should().BeEmpty();
    }

    // =========================================================================
    // Max message size clamp (SmtpServer takes an int)
    // =========================================================================

    [Fact]
    public void EffectiveMaxMessageSize_AboveIntMax_ClampsAndWarns()
    {
        var logger = new FakeLogger<SmtpRelayService>();

        var result = SmtpRelayService.EffectiveMaxMessageSize(3_000_000_000L, logger);

        result.Should().Be(int.MaxValue);
        logger.HasEntry(LogLevel.Warning, "clamped").Should().BeTrue(
            "the advertised EHLO SIZE differs from the configured value — the operator must know");
    }

    [Fact]
    public void EffectiveMaxMessageSize_WithinRange_PassesThroughSilently()
    {
        var logger = new FakeLogger<SmtpRelayService>();

        SmtpRelayService.EffectiveMaxMessageSize(26_214_400L, logger).Should().Be(26_214_400);
        logger.Entries.Should().BeEmpty();
    }
}
