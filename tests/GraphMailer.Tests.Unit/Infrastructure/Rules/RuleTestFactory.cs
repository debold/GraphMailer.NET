using System.Text;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Rules;
using MimeKit;

namespace GraphMailer.Tests.Unit.Infrastructure.Rules;

/// <summary>
/// Message and context builders shared by the rule test suites.
///
/// Everything goes through real <see cref="MimeMessage"/> instances rather than hand-written
/// MIME strings wherever the shape matters, so the tests exercise the same object graph the
/// splitter and the delivery path see.
/// </summary>
internal static class RuleTestFactory
{
    internal static RuleSessionFacts Session(
        string clientIp = "10.0.0.5",
        int listenerPort = 25,
        string authUser = "",
        bool authenticated = false,
        bool tls = false)
        => new()
        {
            ClientIp = clientIp,
            ListenerPort = listenerPort,
            AuthUser = authUser,
            Authenticated = authenticated,
            Tls = tls,
        };

    /// <summary>A plain-text message.</summary>
    internal static MimeMessage TextMessage(
        string from = "sender@example.com",
        string to = "rcpt@example.com",
        string subject = "Hello",
        string body = "Original body")
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };
        return message;
    }

    /// <summary>An HTML-only message.</summary>
    internal static MimeMessage HtmlMessage(
        string html = "<html><body><p>Original</p></body></html>",
        string subject = "Hello")
    {
        var message = TextMessage(subject: subject);
        message.Body = new TextPart("html") { Text = html };
        return message;
    }

    /// <summary>multipart/alternative with both renderings — the common real-world shape.</summary>
    internal static MimeMessage AlternativeMessage(
        string text = "Original text",
        string html = "<html><body><p>Original html</p></body></html>")
    {
        var message = TextMessage();
        var alternative = new MultipartAlternative
        {
            new TextPart("plain") { Text = text },
            new TextPart("html") { Text = html },
        };
        message.Body = alternative;
        return message;
    }

    /// <summary>multipart/mixed: a body plus named attachments.</summary>
    internal static MimeMessage WithAttachments(
        MimeEntity? body = null,
        params (string Name, string ContentType, int SizeBytes)[] attachments)
    {
        var message = TextMessage();
        var mixed = new Multipart("mixed") { body ?? new TextPart("plain") { Text = "Original body" } };

        foreach (var (name, contentType, size) in attachments)
        {
            var parts = contentType.Split('/');
            var part = new MimePart(parts[0], parts[1])
            {
                Content = new MimeContent(new MemoryStream(new byte[size])),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = name },
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = name,
            };
            mixed.Add(part);
        }

        message.Body = mixed;
        return message;
    }

    /// <summary>An inline, cid-referenced image inside a multipart/related.</summary>
    internal static MimeMessage WithInlineImage(string name = "logo.png", string contentId = "logo")
    {
        var message = TextMessage();
        var image = new MimePart("image", "png")
        {
            Content = new MimeContent(new MemoryStream(new byte[64])),
            ContentDisposition = new ContentDisposition(ContentDisposition.Inline) { FileName = name },
            ContentId = contentId,
            FileName = name,
        };
        message.Body = new Multipart("related")
        {
            new TextPart("html") { Text = $"<html><body><img src=\"cid:{contentId}\"></body></html>" },
            image,
        };
        return message;
    }

    /// <summary>A message whose top level claims S/MIME or PGP protection.</summary>
    internal static MimeMessage ProtectedMessage(string multipartSubtype, string protocol)
    {
        var message = TextMessage();
        var container = new Multipart(multipartSubtype)
        {
            new TextPart("plain") { Text = "Signed or encrypted content" },
            new MimePart(protocol.Split('/')[0], protocol.Split('/')[1])
            {
                Content = new MimeContent(new MemoryStream(new byte[32])),
            },
        };
        message.Body = container;
        return message;
    }

    /// <summary>
    /// A message that exercises every condition field at once: both body renderings, an
    /// attachment, a custom header, Cc and Reply-To, and a non-default importance.
    ///
    /// One message rather than one per field, so the coverage test compares like with like and a
    /// field cannot pass only because its case was built around it.
    /// </summary>
    internal static MimeMessage RichMessage()
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("sender@example.com"));
        message.To.Add(MailboxAddress.Parse("rcpt@example.com"));
        message.Cc.Add(MailboxAddress.Parse("cc@example.com"));
        message.ReplyTo.Add(MailboxAddress.Parse("support@example.com"));
        message.Subject = "Quarterly report";
        message.Importance = MessageImportance.High;
        message.Headers.Add("X-Origin", "erp");

        var attachment = new MimePart("application", "pdf")
        {
            Content = new MimeContent(new MemoryStream(new byte[2048])),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "report.pdf" },
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = "report.pdf",
        };

        message.Body = new Multipart("mixed")
        {
            new MultipartAlternative
            {
                new TextPart("plain") { Text = "the plain body" },
                new TextPart("html") { Text = "<html><body>the html body</body></html>" },
            },
            attachment,
        };

        return message;
    }

    /// <summary>Context around <see cref="RichMessage"/>, with session facts to match.</summary>
    internal static MessageRuleContext RichContext()
        => MessageRuleContext.Create(
            Serialise(RichMessage()),
            "sender@example.com",
            ["rcpt@example.com", "extra@partner.test"],
            Session(clientIp: "10.20.5.7", listenerPort: 587, authUser: "relay-user",
                    authenticated: true, tls: true));

    internal static MessageRuleContext Context(
        MimeMessage? message = null,
        string envelopeFrom = "sender@example.com",
        IReadOnlyList<string>? recipients = null,
        RuleSessionFacts? session = null,
        long maxBodyScanBytes = 1_048_576)
        => MessageRuleContext.FromMessage(
            message ?? TextMessage(),
            envelopeFrom,
            recipients ?? ["rcpt@example.com"],
            session ?? Session(),
            sizeBytes: 1024,
            maxBodyScanBytes: maxBodyScanBytes);

    /// <summary>Round-trips a message through bytes, the way the SMTP path does.</summary>
    internal static MessageRuleContext ContextFromBytes(
        MimeMessage message,
        string envelopeFrom = "sender@example.com",
        IReadOnlyList<string>? recipients = null,
        RuleSessionFacts? session = null)
        => MessageRuleContext.Create(
            Serialise(message), envelopeFrom, recipients ?? ["rcpt@example.com"], session ?? Session());

    internal static byte[] Serialise(MimeMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return stream.ToArray();
    }

    internal static string AsText(MimeMessage message) => Encoding.UTF8.GetString(Serialise(message));

    internal static MessageRule Rule(
        string name = "Test rule",
        MessageRuleMode mode = MessageRuleMode.Enforce,
        ConditionMatch match = ConditionMatch.All,
        bool stopProcessing = false,
        bool enabled = true,
        IEnumerable<RuleCondition>? conditions = null,
        IEnumerable<RuleAction>? actions = null)
        => new()
        {
            Name = name,
            Mode = mode,
            Match = match,
            StopProcessing = stopProcessing,
            Enabled = enabled,
            Conditions = [.. conditions ?? []],
            Actions = [.. actions ?? []],
        };

    internal static MessageRulesOptions Options(params MessageRule[] rules)
        => new() { Enabled = true, Rules = [.. rules] };

    internal static RuleCondition Condition(
        RuleConditionField field,
        RuleConditionOperator op,
        string value = "",
        bool negate = false,
        string? headerName = null,
        bool caseSensitive = false)
        => new()
        {
            Field = field,
            Operator = op,
            Value = value,
            Negate = negate,
            HeaderName = headerName,
            CaseSensitive = caseSensitive,
        };
}
