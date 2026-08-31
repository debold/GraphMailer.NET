using System.IO;
using MimeKit;

namespace GraphMailer.ConfigTool.Helpers;

/// <summary>One attachment of a sample message.</summary>
/// <param name="FileName">Name as it would appear in Content-Disposition.</param>
/// <param name="SizeBytes">Content size; the bytes themselves are zeros.</param>
internal readonly record struct SampleAttachment(string FileName, int SizeBytes);

/// <summary>
/// Builds a real <see cref="MimeMessage"/> from the rule tester's form.
///
/// The point is that the tester has one input path: whether the operator loaded an .eml or typed
/// a few fields, the engine sees an actual parsed message. A tester that evaluated conditions
/// against form fields directly would answer a different question than the service does.
/// </summary>
/// <summary>How a sample message is cryptographically protected, if at all.</summary>
internal enum SampleProtection { None, Signed, Encrypted }

internal static class SampleMessageBuilder
{
    internal static MimeMessage Build(
        string from,
        IEnumerable<string> recipients,
        string subject,
        string? bodyText,
        string? bodyHtml,
        IEnumerable<SampleAttachment>? attachments = null,
        IEnumerable<(string Name, string Value)>? headers = null,
        MessageImportance importance = MessageImportance.Normal,
        SampleProtection protection = SampleProtection.None,
        IEnumerable<string>? cc = null)
    {
        var message = new MimeMessage();

        if (TryParse(from, out var sender))
            message.From.Add(sender);

        // To and Cc become headers. Bcc deliberately has no parameter: a blind copy exists only
        // in the envelope — the caller adds it there — because that is exactly how it reaches a
        // recipient without appearing anywhere in the message.
        foreach (var recipient in recipients)
        {
            if (TryParse(recipient, out var mailbox))
                message.To.Add(mailbox);
        }

        foreach (var copy in cc ?? [])
        {
            if (TryParse(copy, out var mailbox))
                message.Cc.Add(mailbox);
        }

        message.Subject = subject ?? string.Empty;
        message.Importance = importance;

        foreach (var (name, value) in headers ?? [])
        {
            if (!string.IsNullOrWhiteSpace(name))
                message.Headers.Add(name.Trim(), value ?? string.Empty);
        }

        message.Body = Protect(BuildBody(bodyText, bodyHtml, attachments), protection);
        return message;
    }

    /// <summary>
    /// Wraps the body in the container shape that marks a message as signed or encrypted.
    ///
    /// A stand-in, not real cryptography — the rules only ever look at the structure, and building
    /// it here is what lets an operator try out "skip the disclaimer on signed mail" without
    /// having to find a genuinely signed message first.
    /// </summary>
    private static MimeEntity Protect(MimeEntity body, SampleProtection protection)
    {
        if (protection == SampleProtection.None) return body;

        var (subtype, mediaSubtype) = protection == SampleProtection.Signed
            ? ("signed", "pkcs7-signature")
            : ("encrypted", "pgp-encrypted");

        return new Multipart(subtype)
        {
            body,
            new MimePart("application", mediaSubtype)
            {
                Content = new MimeContent(new MemoryStream(new byte[64])),
            },
        };
    }

    private static MimeEntity BuildBody(
        string? text, string? html, IEnumerable<SampleAttachment>? attachments)
    {
        var hasText = !string.IsNullOrEmpty(text);
        var hasHtml = !string.IsNullOrEmpty(html);

        // Which parts exist decides what a body rule can do at all, so the builder mirrors the
        // real shapes rather than always producing an alternative.
        MimeEntity body = (hasText, hasHtml) switch
        {
            (true, true) => new MultipartAlternative
            {
                new TextPart("plain") { Text = text ?? string.Empty },
                new TextPart("html") { Text = html ?? string.Empty },
            },
            (false, true) => new TextPart("html") { Text = html ?? string.Empty },
            _ => new TextPart("plain") { Text = text ?? string.Empty },
        };

        var files = (attachments ?? []).Where(a => !string.IsNullOrWhiteSpace(a.FileName)).ToList();
        if (files.Count == 0)
            return body;

        var mixed = new Multipart("mixed") { body };
        foreach (var file in files)
        {
            mixed.Add(new MimePart("application", "octet-stream")
            {
                Content = new MimeContent(new MemoryStream(new byte[Math.Max(0, file.SizeBytes)])),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = file.FileName },
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = file.FileName,
            });
        }
        return mixed;
    }

    private static bool TryParse(string? address, out MailboxAddress mailbox)
    {
        try
        {
            mailbox = MailboxAddress.Parse(address ?? string.Empty);
            return !string.IsNullOrWhiteSpace(mailbox.Address);
        }
        catch (ParseException)
        {
            mailbox = null!;
            return false;
        }
    }

    /// <summary>
    /// Splits a multi-line recipient box into addresses. Blank lines are ignored so a trailing
    /// newline does not become an empty recipient.
    /// </summary>
    internal static IReadOnlyList<string> ParseRecipients(string? text)
        => [.. (text ?? string.Empty)
            .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>Default size for an attachment entered without one.</summary>
    internal const int DefaultAttachmentSizeBytes = 1024;

    /// <summary>
    /// Parses the attachment box: one attachment per line, as <c>name</c> or <c>name | bytes</c>.
    ///
    /// The size is worth accepting because attachment rules can match on it — without a way to
    /// say how big a test attachment is, a size rule cannot be tried out at all. A line without a
    /// size gets <see cref="DefaultAttachmentSizeBytes"/>, and an unparsable size falls back to
    /// the same rather than dropping the attachment, which would look like the rule failing.
    /// </summary>
    internal static IReadOnlyList<SampleAttachment> ParseAttachments(string? text)
    {
        var result = new List<SampleAttachment>();

        foreach (var line in (text ?? string.Empty)
                     .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|', 2);
            var name = parts[0].Trim();
            if (name.Length == 0) continue;

            var size = parts.Length == 2 && int.TryParse(parts[1].Trim(), out var parsed) && parsed >= 0
                ? parsed
                : DefaultAttachmentSizeBytes;

            result.Add(new SampleAttachment(name, size));
        }

        return result;
    }

    /// <summary>
    /// Renders attachments back into the box's format, so loading a message fills the field with
    /// something the operator can then edit.
    /// </summary>
    internal static string FormatAttachments(IEnumerable<(string Name, long SizeBytes)> attachments)
        => string.Join(Environment.NewLine, attachments.Select(a => $"{a.Name} | {a.SizeBytes}"));

    /// <summary>
    /// Parses the headers box: one header per line, as <c>Name: value</c>.
    ///
    /// Without this a <c>Header</c> condition cannot be tried out from the form at all — the only
    /// way to test one would be to find a real message that happens to carry the header.
    /// A line with no colon is skipped rather than becoming a header with an empty name.
    /// </summary>
    internal static IReadOnlyList<(string Name, string Value)> ParseHeaders(string? text)
    {
        var result = new List<(string, string)>();

        foreach (var line in (text ?? string.Empty)
                     .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (name.Length > 0) result.Add((name, value));
        }

        return result;
    }

    /// <summary>Renders headers back into the box's format.</summary>
    internal static string FormatHeaders(IEnumerable<(string Name, string Value)> headers)
        => string.Join(Environment.NewLine, headers.Select(h => $"{h.Name}: {h.Value}"));
}
