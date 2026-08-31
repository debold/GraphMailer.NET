using System.Text.Json;
using FluentAssertions;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace GraphMailer.Tests.Integration.Smtp;

/// <summary>
/// End-to-end behaviour of the message rules over a real SMTP session.
///
/// The unit tests cover the decision logic; what these add is the wire contract and what ends up
/// on disk — the two things a client and an operator actually observe. A rule that computes the
/// right answer is still a bug if the client is told to retry a permanent rejection forever, or
/// if a manipulation reaches the archived message but not the envelope that decides delivery.
/// </summary>
[Collection("SmtpIntegration")]
public class SmtpMessageRuleTests
{
    private static MimeMessage BuildMessage(string subject = "rule integration")
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        message.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = "body" };
        return message;
    }

    private static MimeMessage BuildMessageWithAttachment(string fileName)
    {
        var message = BuildMessage();
        var mixed = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "body" },
            new MimePart("application", "octet-stream")
            {
                Content = new MimeContent(new MemoryStream(new byte[256])),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = fileName },
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = fileName,
            },
        };
        message.Body = mixed;
        return message;
    }

    private static async Task<Exception?> TrySendAsync(SmtpTestHost host, MimeMessage? message = null)
    {
        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);
        try
        {
            await client.SendAsync(message ?? BuildMessage());
            await client.DisconnectAsync(quit: true);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static MessageRule Rule(string name, params RuleAction[] actions)
        => new() { Name = name, Mode = MessageRuleMode.Enforce, Actions = [.. actions] };

    private static MailMetadata ReadMeta(SmtpTestHost host)
    {
        var path = Directory.GetFiles(host.QueueDirectory, "*.meta.json").Should().ContainSingle().Subject;
        return JsonSerializer.Deserialize(File.ReadAllText(path), MailMetadataJsonContext.Default.MailMetadata)!;
    }

    private static string ReadEml(SmtpTestHost host)
        => File.ReadAllText(Directory.GetFiles(host.QueueDirectory, "*.eml").Should().ContainSingle().Subject);

    // =========================================================================
    // Verdicts on the wire
    // =========================================================================

    [Fact]
    public async Task Reject_ClientGetsTheConfiguredStatusAndNothingIsQueued()
    {
        await using var host = await SmtpTestHost.StartAsync(messageRules:
        [
            Rule("block", new RuleAction
            {
                Type = RuleActionType.Reject,
                SmtpCode = 554,
                Value = "Not accepted by policy",
            }),
        ]);

        var error = await TrySendAsync(host);

        error.Should().BeOfType<SmtpCommandException>("a policy rejection is permanent, not transient");
        ((SmtpCommandException)error!).StatusCode.Should().Be(SmtpStatusCode.TransactionFailed);
        Directory.GetFiles(host.QueueDirectory, "*").Should().BeEmpty(
            "a rejected message must not leave anything behind in the queue");
    }

    [Fact]
    public async Task Discard_ClientIsTold250ButNothingIsQueued()
    {
        await using var host = await SmtpTestHost.StartAsync(messageRules:
            [Rule("sink", new RuleAction { Type = RuleActionType.Discard })]);

        var error = await TrySendAsync(host);

        error.Should().BeNull("a discard is silent — the client sees an ordinary acceptance");
        Directory.GetFiles(host.QueueDirectory, "*").Should().BeEmpty();

        Directory.GetFiles(host.BlockedDirectory, "*.meta.json").Should().ContainSingle(
            "the record is the only trace a discarded message leaves");
    }

    [Fact]
    public async Task RulesDisabled_MessageIsQueuedUnchanged()
    {
        await using var host = await SmtpTestHost.StartAsync();

        var error = await TrySendAsync(host);

        error.Should().BeNull();
        ReadMeta(host).Subject.Should().Be("rule integration");
    }

    // =========================================================================
    // Manipulation reaches the queued message and the envelope
    // =========================================================================

    [Fact]
    public async Task PrefixSubject_ChangesTheQueuedMessageAndItsMetadata()
    {
        await using var host = await SmtpTestHost.StartAsync(messageRules:
        [
            Rule("tag", new RuleAction { Type = RuleActionType.PrefixSubject, Value = "[EXTERNAL] " }),
        ]);

        (await TrySendAsync(host)).Should().BeNull();

        ReadEml(host).Should().Contain("[EXTERNAL] rule integration");
        ReadMeta(host).Subject.Should().Be("[EXTERNAL] rule integration");
    }

    [Fact]
    public async Task PrefixSubject_KeepsTheTrailingSpaceAllTheWayToTheQueue()
    {
        // Regression: the prefix travels through the config file, the binder and the rule engine.
        // A trim anywhere along that path turns "[EXTERNAL] " into "[EXTERNAL]" and glues the tag
        // onto the subject.
        await using var host = await SmtpTestHost.StartAsync(messageRules:
        [
            Rule("tag", new RuleAction { Type = RuleActionType.PrefixSubject, Value = "[EXTERNAL] " }),
        ]);

        (await TrySendAsync(host)).Should().BeNull();

        ReadMeta(host).Subject.Should().Be("[EXTERNAL] rule integration");
    }

    [Fact]
    public async Task SuffixSubject_KeepsTheLeadingSpace()
    {
        await using var host = await SmtpTestHost.StartAsync(messageRules:
        [
            Rule("tag", new RuleAction { Type = RuleActionType.SuffixSubject, Value = " (unverified)" }),
        ]);

        (await TrySendAsync(host)).Should().BeNull();

        ReadMeta(host).Subject.Should().Be("rule integration (unverified)");
    }

    [Fact]
    public async Task AddBccRecipient_ReachesTheEnvelopeButNotTheMessage()
    {
        // Delivery follows the envelope. A Bcc header would be ignored on delivery and would
        // leak the blind copy into the archived message, so it must not be written.
        await using var host = await SmtpTestHost.StartAsync(messageRules:
        [
            Rule("archive", new RuleAction
            {
                Type = RuleActionType.AddRecipient,
                Recipient = RecipientKind.Bcc,
                Value = "archive@example.com",
            }),
        ]);

        (await TrySendAsync(host)).Should().BeNull();

        ReadMeta(host).To.Should().BeEquivalentTo(["recipient@example.com", "archive@example.com"]);
        ReadEml(host).Should().NotContain("archive@example.com");
    }

    [Fact]
    public async Task RemoveRecipient_LeavesTheEnvelopeAndTheHeader()
    {
        // Removing from the header alone would turn the recipient into a Bcc — still delivered,
        // now invisibly.
        await using var host = await SmtpTestHost.StartAsync(messageRules:
        [
            Rule("drop", new RuleAction { Type = RuleActionType.RemoveRecipient, Match = "recipient@example.com" }),
            Rule("keep-one", new RuleAction
            {
                Type = RuleActionType.AddRecipient,
                Recipient = RecipientKind.To,
                Value = "replacement@example.com",
            }),
        ]);

        (await TrySendAsync(host)).Should().BeNull();

        ReadMeta(host).To.Should().BeEquivalentTo(["replacement@example.com"]);
        ReadEml(host).Should().NotContain("recipient@example.com");
    }

    [Fact]
    public async Task RemoveAttachments_DropsThemFromTheQueuedMessageAndTheCount()
    {
        await using var host = await SmtpTestHost.StartAsync(messageRules:
        [
            Rule("strip", new RuleAction
            {
                Type = RuleActionType.RemoveAttachments,
                AttachmentMatch = AttachmentMatchMode.Extension,
                Value = ".docm",
            }),
        ]);

        (await TrySendAsync(host, BuildMessageWithAttachment("macro.docm"))).Should().BeNull();

        ReadMeta(host).AttachmentCount.Should().Be(0);
        ReadEml(host).Should().NotContain("macro.docm");
    }

    [Fact]
    public async Task AddHeader_ReachesTheQueuedMessage()
    {
        await using var host = await SmtpTestHost.StartAsync(messageRules:
        [
            Rule("stamp", new RuleAction
            {
                Type = RuleActionType.AddHeader,
                HeaderName = "X-GraphMailer-Policy",
                Value = "external",
            }),
        ]);

        (await TrySendAsync(host)).Should().BeNull();

        ReadEml(host).Should().Contain("X-GraphMailer-Policy: external");
    }

    // =========================================================================
    // Audit mode and rule ordering
    // =========================================================================

    [Fact]
    public async Task AuditRule_QueuesTheMessageUnchanged()
    {
        await using var host = await SmtpTestHost.StartAsync(messageRules:
        [
            new MessageRule
            {
                Name = "watch",
                Mode = MessageRuleMode.Audit,
                Actions = [new RuleAction { Type = RuleActionType.PrefixSubject, Value = "[TAG] " }],
            },
        ]);

        (await TrySendAsync(host)).Should().BeNull();

        ReadMeta(host).Subject.Should().Be("rule integration", "audit mode changes nothing");
    }

    [Fact]
    public async Task StopProcessing_PreventsLaterRules()
    {
        await using var host = await SmtpTestHost.StartAsync(messageRules:
        [
            new MessageRule
            {
                Name = "first",
                Mode = MessageRuleMode.Enforce,
                StopProcessing = true,
                Actions = [new RuleAction { Type = RuleActionType.PrefixSubject, Value = "A" }],
            },
            Rule("second", new RuleAction { Type = RuleActionType.PrefixSubject, Value = "B" }),
        ]);

        (await TrySendAsync(host)).Should().BeNull();

        ReadMeta(host).Subject.Should().Be("Arule integration");
    }

    [Fact]
    public async Task ConditionThatDoesNotMatch_LeavesTheMessageAlone()
    {
        await using var host = await SmtpTestHost.StartAsync(messageRules:
        [
            new MessageRule
            {
                Name = "partners only",
                Mode = MessageRuleMode.Enforce,
                Conditions =
                [
                    new RuleCondition
                    {
                        Field = RuleConditionField.EnvelopeRecipient,
                        Operator = RuleConditionOperator.DomainIs,
                        Value = "@partner.test",
                    },
                ],
                Actions = [new RuleAction { Type = RuleActionType.PrefixSubject, Value = "[P] " }],
            },
        ]);

        (await TrySendAsync(host)).Should().BeNull();

        ReadMeta(host).Subject.Should().Be("rule integration");
    }
}
