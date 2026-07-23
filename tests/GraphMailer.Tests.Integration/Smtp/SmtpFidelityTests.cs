using System.Text.Json;
using GraphMailer.Service.Services;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace GraphMailer.Tests.Integration.Smtp;

/// <summary>
/// End-to-end fidelity tests: a real SMTP session delivers a message to a real queue, the
/// queued .eml + .meta.json are read back from disk, and the result is run through the same
/// translation the queue processor uses (<see cref="GraphApiClient.BuildMessage"/>).
///
/// This is the only place where the whole chain — SMTP envelope, MailQueueWriter, MIME
/// parsing, Graph mapping — is exercised together. The unit tests assert the translation in
/// isolation; only here does the envelope actually come from RCPT TO instead of a literal
/// in the test. No tenant required.
/// </summary>
[Collection("SmtpIntegration")]
public class SmtpFidelityTests
{
    /// <summary>
    /// Delivers <paramref name="message"/> over a real SMTP session, optionally with envelope
    /// recipients that differ from the To:/Cc: headers, then returns the queued message
    /// translated exactly as the queue processor would translate it.
    /// </summary>
    private static async Task<Microsoft.Graph.Models.Message> RelayAndTranslateAsync(
        SmtpTestHost host,
        MimeMessage message,
        IEnumerable<MailboxAddress>? envelopeRecipients = null)
    {
        using (var client = new SmtpClient())
        {
            await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

            if (envelopeRecipients is null)
                await client.SendAsync(message);
            else
                await client.SendAsync(message, message.From.Mailboxes.First(), envelopeRecipients);

            await client.DisconnectAsync(quit: true);
        }

        var emlPath = Directory.GetFiles(host.QueueDirectory, "*.eml").Should().ContainSingle(
            "the message must have been queued").Subject;
        var metaPath = Path.ChangeExtension(emlPath, null) + ".meta.json";

        var meta = JsonSerializer.Deserialize<MailMetadata>(
            await File.ReadAllTextAsync(metaPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        // meta.To is what the client actually sent as RCPT TO — the envelope, not the headers.
        var queued = await MimeMessage.LoadAsync(emlPath);
        return GraphApiClient.BuildMessage(queued, [], meta.To);
    }

    private static MimeMessage BuildMessage(Action<MimeMessage>? mutate = null)
    {
        var message = new MimeMessage
        {
            From = { new MailboxAddress("Sender", "sender@example.com") },
            To = { new MailboxAddress("Recipient", "recipient@example.com") },
            Subject = "Fidelity integration test",
            Body = new TextPart("plain") { Text = "Fidelity test body." },
        };
        mutate?.Invoke(message);
        return message;
    }

    // -------------------------------------------------------------------------
    // Envelope decides delivery, headers do not
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HeaderRecipientWithoutRcptTo_IsNotDeliveredTo()
    {
        // The To: header lists two addresses but the client sends RCPT TO for only one —
        // exactly what a client doing per-domain splitting does. The second address must
        // not receive the message, or the relay would deliver mail nobody asked it to.
        await using var host = await SmtpTestHost.StartAsync(authRequired: false);

        var message = BuildMessage(m => m.To.Add(new MailboxAddress("Ghost", "ghost@example.com")));

        var graph = await RelayAndTranslateAsync(host, message,
            [MailboxAddress.Parse("recipient@example.com")]);

        graph.ToRecipients.Should().ContainSingle()
            .Which.EmailAddress!.Address.Should().Be("recipient@example.com");
        graph.ToRecipients.Should().NotContain(r => r.EmailAddress!.Address == "ghost@example.com");
        graph.BccRecipients.Should().BeEmpty();
    }

    [Fact]
    public async Task RcptToWithoutHeaderEntry_BecomesBccRecipient()
    {
        // The mirror case: a real Bcc. The client strips the Bcc header before sending, so
        // the address exists only in the envelope and must still be delivered to.
        await using var host = await SmtpTestHost.StartAsync(authRequired: false);

        var graph = await RelayAndTranslateAsync(host, BuildMessage(),
            [MailboxAddress.Parse("recipient@example.com"), MailboxAddress.Parse("blind@example.com")]);

        graph.ToRecipients.Should().ContainSingle(r => r.EmailAddress!.Address == "recipient@example.com");
        graph.BccRecipients.Should().ContainSingle()
            .Which.EmailAddress!.Address.Should().Be("blind@example.com");
    }

    [Fact]
    public async Task EveryEnvelopeRecipient_EndsUpInExactlyOneField()
    {
        // The no-loss guarantee: To + Cc + Bcc together must reproduce the envelope exactly.
        await using var host = await SmtpTestHost.StartAsync(authRequired: false);

        var message = BuildMessage(m => m.Cc.Add(new MailboxAddress("Copy", "cc@example.com")));
        string[] envelope = ["recipient@example.com", "cc@example.com", "blind@example.com"];

        var graph = await RelayAndTranslateAsync(host, message, envelope.Select(MailboxAddress.Parse));

        var delivered = graph.ToRecipients!.Concat(graph.CcRecipients!).Concat(graph.BccRecipients!)
            .Select(r => r.EmailAddress!.Address!)
            .ToList();

        delivered.Should().BeEquivalentTo(envelope, "no envelope recipient may be lost or duplicated");
        delivered.Should().OnlyHaveUniqueItems("an address must not be delivered to twice");
    }

    // -------------------------------------------------------------------------
    // Message properties survive the whole chain
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SensitivityPrivate_SurvivesTheWholeChain()
    {
        await using var host = await SmtpTestHost.StartAsync(authRequired: false);

        var message = BuildMessage(m => m.Headers.Add("Sensitivity", "Private"));

        var graph = await RelayAndTranslateAsync(host, message);

        graph.SingleValueExtendedProperties.Should().Contain(
            p => p.Id == "Integer 0x0036" && p.Value == "2",
            "the private marking has no Graph property — PidTagSensitivity is its only carrier");
    }

    [Fact]
    public async Task ReplyTo_SurvivesTheWholeChain_AndIsNotEnvelopeFiltered()
    {
        // Reply-To is not a delivery target, so it must NOT be filtered against the envelope
        // the way To:/Cc: are. Regression guard: the two now sit next to each other in
        // BuildMessage, and "making it consistent" would silently break every reply.
        await using var host = await SmtpTestHost.StartAsync(authRequired: false);

        var message = BuildMessage(m =>
            m.ReplyTo.Add(new MailboxAddress("Support Desk", "support@example.com")));

        var graph = await RelayAndTranslateAsync(host, message,
            [MailboxAddress.Parse("recipient@example.com")]);

        var replyTo = graph.ReplyTo.Should().ContainSingle().Subject;
        replyTo.EmailAddress!.Address.Should().Be("support@example.com",
            "Reply-To is never in the envelope and must survive regardless");
        replyTo.EmailAddress.Name.Should().Be("Support Desk", "the display name is part of the address");
    }

    [Fact]
    public async Task ImportanceReceiptsAndSender_SurviveTheWholeChain()
    {
        await using var host = await SmtpTestHost.StartAsync(authRequired: false);

        var message = BuildMessage(m =>
        {
            m.Importance = MessageImportance.High;
            m.Sender = new MailboxAddress("Shared Mailbox", "shared@example.com");
            m.Headers.Add("Disposition-Notification-To", "sender@example.com");
            m.Headers.Add("Return-Receipt-To", "sender@example.com");
        });

        var graph = await RelayAndTranslateAsync(host, message);

        graph.Importance.Should().Be(Microsoft.Graph.Models.Importance.High);
        graph.Sender!.EmailAddress!.Address.Should().Be("shared@example.com");
        graph.IsReadReceiptRequested.Should().BeTrue();
        graph.IsDeliveryReceiptRequested.Should().BeTrue();
    }

    [Fact]
    public async Task ManyCustomHeaders_AreCappedSoTheMessageStaysDeliverable()
    {
        // Graph rejects more than five custom headers with 400, which the queue treats as
        // permanent — uncapped, this message would be NDR'd instead of delivered.
        await using var host = await SmtpTestHost.StartAsync(authRequired: false);

        var message = BuildMessage(m =>
        {
            for (var i = 1; i <= 9; i++)
                m.Headers.Add($"X-Scanner-{i}", $"value-{i}");
        });

        var graph = await RelayAndTranslateAsync(host, message);

        graph.InternetMessageHeaders.Should().HaveCount(GraphApiClient.MaxCustomHeaders);
    }
}
