using GraphMailer.ConfigTool.Helpers;
using MimeKit;

namespace GraphMailer.Tests.Unit.ConfigTool;

/// <summary>
/// The rule tester's before/after comparison.
///
/// The point of this class is subtraction: the tester used to print the whole message twice and
/// leave the reader to spot the difference. Everything unchanged is noise, so what matters here
/// is that unchanged fields stay out of the result — and that a real change never does.
/// </summary>
public sealed class MessageSnapshotTests
{
    private static MimeMessage Message(
        string from = "sender@example.com",
        string to = "rcpt@example.com",
        string subject = "Quarterly report",
        string body = "Message body")
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };
        return message;
    }

    private static MessageSnapshot Capture(MimeMessage message, params string[] recipients)
        => MessageSnapshot.Capture(
            message, "sender@example.com", recipients.Length > 0 ? recipients : ["rcpt@example.com"]);

    // =========================================================================
    // Nothing changed
    // =========================================================================

    [Fact]
    public void Diff_IdenticalSnapshots_ReportsNothing()
    {
        var snapshot = Capture(Message());

        MessageSnapshot.Diff(snapshot, snapshot).Should().BeEmpty();
    }

    [Fact]
    public void Diff_UnrelatedFieldsStaySilent()
    {
        // One message, mutated — which is what the tester really does: the rules change the
        // message in place, so before and after describe the same object at two points in time.
        var message = Message(subject: "Before");
        var before = Capture(message);

        message.Subject = "After";
        var after = Capture(message);

        MessageSnapshot.Diff(before, after).Should().ContainSingle()
            .Which.Field.Should().Be("Subject", "From, recipients, attachments and bodies did not move");
    }

    // =========================================================================
    // Real changes
    // =========================================================================

    [Fact]
    public void Diff_Subject_ReportsBothSides()
    {
        var message = Message(subject: "Quarterly report");
        var before = Capture(message);

        message.Subject = "[EXTERNAL] Quarterly report";
        var after = Capture(message);

        var change = MessageSnapshot.Diff(before, after).Should().ContainSingle().Subject;
        change.Before.Should().Be("Quarterly report");
        change.After.Should().Be("[EXTERNAL] Quarterly report");
    }

    [Fact]
    public void Diff_AddedEnvelopeRecipient_ShowsAsEnvelopeAndBcc()
    {
        // A blind copy is exactly this: in the envelope, in no header.
        var message = Message();
        var before = Capture(message, "rcpt@example.com");
        var after = Capture(message, "rcpt@example.com", "archive@example.com");

        var fields = MessageSnapshot.Diff(before, after).Select(c => c.Field).ToList();

        fields.Should().Contain("Envelope");
        fields.Should().Contain("Bcc");
        fields.Should().NotContain("To", "the header did not change");
    }

    [Fact]
    public void Diff_ReorderedRecipients_IsNotAChange()
    {
        var message = Message();
        var before = Capture(message, "a@example.com", "b@example.com");
        var after = Capture(message, "b@example.com", "a@example.com");

        MessageSnapshot.Diff(before, after).Should().NotContain(c => c.Field == "Envelope");
    }

    [Fact]
    public void Diff_AddedHeader_ReportsOnlyThatHeader()
    {
        // A message carries dozens of headers; printing them all because one was added would
        // bury the one thing that happened.
        var message = Message();
        var before = Capture(message);

        message.Headers.Add("X-Policy", "external");
        var after = Capture(message);

        var change = MessageSnapshot.Diff(before, after).Should().ContainSingle().Subject;
        change.Field.Should().Be("Header");
        change.After.Should().Be("X-Policy: external");
    }

    [Fact]
    public void Diff_ChangedHeaderValue_PairsTheOldAndNewValue()
    {
        var message = Message();
        message.Headers.Add("X-Origin", "erp");
        var before = Capture(message);

        message.Headers.Remove("X-Origin");
        message.Headers.Add("X-Origin", "crm");
        var after = Capture(message);

        var change = MessageSnapshot.Diff(before, after).Should().ContainSingle().Subject;

        change.Before.Should().Be("X-Origin: erp");
        change.After.Should().Be("X-Origin: crm");
    }

    [Fact]
    public void Diff_RemovedHeader_SaysSo()
    {
        var message = Message();
        message.Headers.Add("X-Origin", "erp");
        var before = Capture(message);

        message.Headers.Remove("X-Origin");
        var change = MessageSnapshot.Diff(before, Capture(message)).Should().ContainSingle().Subject;

        change.Before.Should().Be("X-Origin: erp");
        change.After.Should().Be("(removed)");
    }

    [Fact]
    public void Diff_Importance_IsReported()
    {
        var message = Message();
        var before = Capture(message);

        message.Importance = MessageImportance.High;

        MessageSnapshot.Diff(before, Capture(message))
            .Should().ContainSingle(c => c.Field == "Importance" && c.After == "High");
    }

    [Fact]
    public void Diff_BodyChange_IsReportedAndShortened()
    {
        var message = Message();
        var before = Capture(message);

        ((TextPart)message.Body).Text = new string('x', 500);
        var change = MessageSnapshot.Diff(before, Capture(message)).Should().ContainSingle().Subject;

        change.Field.Should().Be("Text body");
        change.After.Length.Should().BeLessThanOrEqualTo(120, "a long body is trimmed");
        change.After.Should().EndWith("…", "the trim is visible rather than silent");
    }

    [Fact]
    public void Diff_EmptyValue_ReadsAsNoneRatherThanBlank()
    {
        var message = Message(subject: "Something");
        var before = Capture(message);

        message.Subject = "";
        MessageSnapshot.Diff(before, Capture(message)).Should().ContainSingle()
            .Which.After.Should().Be("(none)");
    }

    // =========================================================================
    // Capture
    // =========================================================================

    [Fact]
    public void Capture_DerivesBccFromTheEnvelope()
    {
        var snapshot = Capture(Message(), "rcpt@example.com", "hidden@example.com");

        snapshot.Bcc.Should().ContainSingle().Which.Should().Be("hidden@example.com");
        snapshot.To.Should().ContainSingle().Which.Should().Be("rcpt@example.com");
    }

    [Fact]
    public void Capture_NamesHeaderAddressesTheEnvelopeDoesNotConfirm()
    {
        // These look like recipients in the message and are dropped on delivery.
        var message = Message();
        message.To.Add(MailboxAddress.Parse("ghost@example.com"));

        var snapshot = Capture(message, "rcpt@example.com");

        snapshot.NotDelivered.Should().ContainSingle().Which.Should().Be("ghost@example.com");
    }

    [Fact]
    public void Capture_ReportsAttachmentsWithTheirSizes()
    {
        var message = Message();
        message.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "body" },
            new MimePart("application", "pdf")
            {
                Content = new MimeContent(new MemoryStream(new byte[2048])),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "report.pdf" },
                FileName = "report.pdf",
            },
        };

        Capture(message).Attachments.Should().ContainSingle()
            .Which.Should().Contain("report.pdf").And.Contain("2048");
    }
}
