using FluentAssertions;
using GraphMailer.Service.Infrastructure.Metrics;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using NSubstitute;

namespace GraphMailer.Tests.Integration.Smtp;

/// <summary>
/// The message history the Messages page shows, as written by a real SMTP session: one trace
/// write per message, once the SMTP outcome is decided, telling the steps in order.
/// </summary>
[Collection("SmtpIntegration")]
public class SmtpMessageTraceTests
{
    private static MimeMessage BuildMessage()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Scanner", "scanner@example.com"));
        message.To.Add(new MailboxAddress("Accounting", "accounting@example.com"));
        message.Subject = "trace integration";
        message.Body = new TextPart("plain") { Text = "body" };
        return message;
    }

    private static async Task SendAsync(SmtpTestHost host, bool expectFailure = false)
    {
        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);
        try
        {
            await client.SendAsync(BuildMessage());
        }
        catch (SmtpCommandException) when (expectFailure)
        {
        }
        await client.DisconnectAsync(quit: true);
    }

    private static (MessageTraceHeader Header, string? Status, IReadOnlyList<MessageTraceEvent> Events) SingleTrace(SmtpTestHost host)
    {
        var call = host.Metrics.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IMetricsService.RecordTraceAsync))
            .Should().ContainSingle("the receipt history is written once, after the SMTP outcome").Subject;
        var args = call.GetArguments();
        return ((MessageTraceHeader)args[0]!, (string?)args[1], (IReadOnlyList<MessageTraceEvent>)args[3]!);
    }

    [Fact]
    public async Task Accepted_message_is_traced_from_receipt_to_queue()
    {
        await using var host = await SmtpTestHost.StartAsync(
            malwareScanner: ScriptedScanner.Clean(), malwareScanMode: "Enforce");

        await SendAsync(host);

        var (header, status, events) = SingleTrace(host);
        status.Should().Be(TraceStatuses.Queued);
        header.From.Should().Be("scanner@example.com");
        header.To.Should().Equal("accounting@example.com");
        header.Subject.Should().Be("trace integration");
        header.ClientIp.Should().Be("127.0.0.1");

        events.Select(e => e.Stage).Should().Equal(TraceStages.Smtp, TraceStages.Scan, TraceStages.Smtp);
        events[0].Summary.Should().StartWith("Received from 127.0.0.1 on port ");
        events[0].Detail.Should().Contain("no TLS").And.Contain("not signed in").And.Contain("1 recipient");
        events[1].Summary.Should().Be("Malware scan: clean");
        events[^1].Summary.Should().Be("Accepted and queued (250 OK)");
        events.Should().BeInAscendingOrder(e => e.OccurredAtUtc);
    }

    [Fact]
    public async Task Malware_rejection_is_traced_as_rejected_with_its_reason()
    {
        await using var host = await SmtpTestHost.StartAsync(
            malwareScanner: ScriptedScanner.DetectsAttachment("invoice.docm", "feedface"),
            malwareScanMode: "Enforce");

        await SendAsync(host, expectFailure: true);

        var (header, status, events) = SingleTrace(host);
        status.Should().Be(TraceStatuses.Rejected);
        header.Subject.Should().Be("trace integration", "the subject is parsed even when the message never reaches the queue");
        events[^1].Outcome.Should().Be(TraceOutcomes.Failed);
        events[^1].Summary.Should().Be("Rejected: malware found in 'invoice.docm'");
        events[^1].Detail.Should().StartWith("554 sent.");
    }

    [Fact]
    public async Task Audit_mode_finding_is_traced_as_a_warning_and_the_message_continues()
    {
        await using var host = await SmtpTestHost.StartAsync(
            malwareScanner: ScriptedScanner.DetectsAttachment("invoice.docm", "feedface"),
            malwareScanMode: "Audit");

        await SendAsync(host);

        var (_, status, events) = SingleTrace(host);
        status.Should().Be(TraceStatuses.Queued);
        events.Should().Contain(e => e.Stage == TraceStages.Scan && e.Outcome == TraceOutcomes.Warning
                                     && e.Summary.Contains("delivered anyway (audit mode)"));
    }
}
