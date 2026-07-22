using MimeKit;

namespace GraphMailer.Service.Services;

/// <summary>
/// Splits a parsed MIME message into exactly one body (HTML and/or plain-text part) and
/// a list of attachment entities. Single source of truth for "what is an attachment" —
/// used by <see cref="GraphApiClient"/> to build the outgoing Graph message and by
/// <see cref="MailQueueWriter"/> for the reception statistics, so the counts recorded at
/// receipt always describe what will actually be delivered.
///
/// Design goal: a relayed message must be delivered as close to byte-identical as the
/// Graph API allows. Every leaf entity therefore ends up in exactly one of three places:
///   1. the message body        (first unnamed, non-attachment text/plain and text/html part)
///   2. the attachment list     (everything else — parts are NEVER silently discarded)
///   3. dropped alternatives    (surplus renderings inside multipart/alternative only;
///                               counted so the caller can log them)
///
/// Attachment classification is deliberately more lenient than MimeKit's
/// <c>MimeMessage.Attachments</c>: RFC 2183 §2.8 requires that an unrecognized
/// Content-Disposition type is treated as "attachment". Legacy mailers get this wrong in
/// the wild — SecureBlackbox 16 emits <c>Content-Disposition: &lt;filename&gt;; filename="…"</c>
/// — and strict matching against the literal token "attachment" silently drops such parts.
/// </summary>
internal static class MimeMessageSplitter
{
    /// <summary>An entity classified as attachment, with its resolved inline flag.</summary>
    internal readonly record struct SplitAttachment(MimeEntity Entity, bool IsInline);

    /// <param name="HtmlBody">The text/html part chosen as message body, if any.</param>
    /// <param name="TextBody">The text/plain part chosen as message body, if any.</param>
    /// <param name="Attachments">Every other leaf entity (MimePart or MessagePart).</param>
    /// <param name="DroppedAlternativeBodies">
    /// Surplus body renderings inside multipart/alternative that no client would display
    /// either; the only entities not carried over — callers should log the count.
    /// </param>
    internal sealed record SplitResult(
        TextPart? HtmlBody,
        TextPart? TextBody,
        IReadOnlyList<SplitAttachment> Attachments,
        int DroppedAlternativeBodies);

    internal static SplitResult Split(MimeMessage message)
    {
        var state = new WalkState();
        if (message.Body is not null)
            Visit(message.Body, state, inAlternative: false);
        return new SplitResult(state.Html, state.Text, state.Attachments, state.DroppedAlternatives);
    }

    /// <summary>
    /// Approximate on-the-wire size of an entity for statistics: the still-encoded
    /// content stream when available (cheap), otherwise the serialized entity.
    /// </summary>
    internal static long MeasureEncodedSize(MimeEntity entity)
    {
        if (entity is MimePart { Content.Stream.CanSeek: true } part)
            return part.Content.Stream.Length;

        var counter = new CountingStream();
        entity.WriteTo(counter);
        return counter.Length;
    }

    private sealed class WalkState
    {
        public TextPart? Html;
        public TextPart? Text;
        public readonly List<SplitAttachment> Attachments = [];
        public int DroppedAlternatives;
    }

    private static void Visit(MimeEntity entity, WalkState state, bool inAlternative)
    {
        switch (entity)
        {
            case MessagePart rfc822:
                // Attached message (message/rfc822): kept as one opaque unit. Recursing
                // into it would hoist the inner mail's parts as attachments of the outer
                // mail and lose the mail itself.
                state.Attachments.Add(new SplitAttachment(rfc822, IsInline: false));
                break;

            case Multipart multipart when multipart.ContentType.IsMimeType("multipart", "alternative"):
                // Alternatives are renderings of the same content, best one last
                // (RFC 2046 §5.1.4) — walk in reverse so the body slots get the richest
                // representation, matching what mail clients display.
                for (var i = multipart.Count - 1; i >= 0; i--)
                    Visit(multipart[i], state, inAlternative: true);
                break;

            case Multipart multipart:
                // mixed/related/signed/report/…: containers only — classify the leaves.
                foreach (var child in multipart)
                    Visit(child, state, inAlternative);
                break;

            case MimePart part:
                VisitLeaf(part, state, inAlternative);
                break;
        }
    }

    private static void VisitLeaf(MimePart part, WalkState state, bool inAlternative)
    {
        var disposition = part.ContentDisposition?.Disposition;

        // RFC 2183 §2.8: any disposition type other than "inline" — including unknown
        // or malformed tokens — must be treated as "attachment". Comparing against the
        // literal "attachment" only (MimeKit's IsAttachment) drops malformed parts.
        var isExplicitAttachment = disposition is not null
            && !disposition.Equals(ContentDisposition.Inline, StringComparison.OrdinalIgnoreCase);

        // Body candidates: unnamed text parts without an attachment disposition. A part
        // that carries a filename (Content-Disposition filename or Content-Type name)
        // announces itself as a file and stays an attachment even without a disposition.
        if (!isExplicitAttachment && part.FileName is null && part is TextPart text)
        {
            if (text.IsHtml && state.Html is null) { state.Html = text; return; }
            if (text.IsPlain && state.Text is null) { state.Text = text; return; }

            if (inAlternative && (text.IsHtml || text.IsPlain))
            {
                // Surplus rendering of an already-captured body inside
                // multipart/alternative — clients drop it too. Outside of
                // alternative, a second text part is content of its own and
                // falls through to the attachment list below.
                state.DroppedAlternatives++;
                return;
            }
        }

        // Inline = explicit "inline" disposition, or no disposition at all but a
        // Content-ID (a cid:-referenced resource, e.g. an embedded image in
        // multipart/related written by mailers that omit the disposition header).
        var isInline = disposition is not null
            ? disposition.Equals(ContentDisposition.Inline, StringComparison.OrdinalIgnoreCase)
            : !string.IsNullOrWhiteSpace(part.ContentId);

        state.Attachments.Add(new SplitAttachment(part, isInline));
    }

    /// <summary>Write-only stream that counts bytes — sizes an entity without buffering it.</summary>
    private sealed class CountingStream : Stream
    {
        private long _length;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => _length += count;
        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;
        public override void WriteByte(byte value) => _length++;
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
