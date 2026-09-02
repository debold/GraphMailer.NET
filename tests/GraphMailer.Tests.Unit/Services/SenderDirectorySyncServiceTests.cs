using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GraphMailer.Tests.Unit.Services;

/// <summary>
/// Startup guard against a configuration the ConfigTool prevents but a hand-edited
/// graphmailer.json can still hold: accepting senders that own no mailbox while there is no relay
/// mailbox to deliver them. Those senders pass MAIL FROM and are then bounced by Graph — the
/// operator gets a delayed NDR where a clean 550 during the SMTP session was available.
/// The log is the only place this surfaces, so it is worth asserting.
/// </summary>
public sealed class SenderDirectorySyncServiceTests
{
    private static IOptionsMonitor<T> Monitor<T>(T value)
    {
        var monitor = Substitute.For<IOptionsMonitor<T>>();
        monitor.CurrentValue.Returns(value);
        return monitor;
    }

    private static (SenderDirectorySyncService Sut, FakeLogger<SenderDirectorySyncService> Logger) Create(
        SenderValidationOptions validation,
        SenderRoutingOptions routing)
    {
        var logger = new FakeLogger<SenderDirectorySyncService>();
        var sut = new SenderDirectorySyncService(
            Substitute.For<ITenantSenderDirectory>(),
            Monitor(validation),
            Monitor(new GraphApiOptions()),
            Monitor(routing),
            logger);
        return (sut, logger);
    }

    private static SenderRoutingOptions WithRelay() =>
        new() { Enabled = true, RelayMailbox = "relay@corp.com" };

    [Fact]
    public void Warn_AcceptsMailboxlessSendersWithoutARelayMailbox_LogsWarning()
    {
        var (sut, logger) = Create(
            new SenderValidationOptions { Enabled = true, AcceptMailboxlessSenders = true },
            new SenderRoutingOptions { Enabled = false });

        sut.WarnAboutUndeliverableAcceptance();

        logger.HasEntry(LogLevel.Warning, "AcceptMailboxlessSenders").Should().BeTrue();
        logger.HasEntry(LogLevel.Warning, "relay mailbox").Should().BeTrue();
    }

    [Fact]
    public void Warn_RoutingOnButMailboxBlank_LogsWarning()
    {
        var (sut, logger) = Create(
            new SenderValidationOptions { Enabled = true, AcceptMailboxlessSenders = true },
            new SenderRoutingOptions { Enabled = true, RelayMailbox = "  " });

        sut.WarnAboutUndeliverableAcceptance();

        logger.HasEntry(LogLevel.Warning, "relay mailbox").Should().BeTrue();
    }

    [Fact]
    public void Warn_RelayMailboxConfigured_StaysSilent()
    {
        var (sut, logger) = Create(
            new SenderValidationOptions { Enabled = true, AcceptMailboxlessSenders = true },
            WithRelay());

        sut.WarnAboutUndeliverableAcceptance();

        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Warn_OptionNotSet_StaysSilent()
    {
        var (sut, logger) = Create(
            new SenderValidationOptions { Enabled = true },
            new SenderRoutingOptions { Enabled = false });

        sut.WarnAboutUndeliverableAcceptance();

        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Warn_ValidationDisabled_StaysSilent()
    {
        // With validation off nothing is rejected at MAIL FROM anyway, so the options do nothing
        // and there is nothing to warn about.
        var (sut, logger) = Create(
            new SenderValidationOptions { Enabled = false, AcceptMailboxlessSenders = true },
            new SenderRoutingOptions { Enabled = false });

        sut.WarnAboutUndeliverableAcceptance();

        logger.Entries.Should().BeEmpty();
    }
}
