namespace GraphMailer.Service.Services;

/// <summary>An image embedded in an HTML email as a CID inline attachment (<c>&lt;img src="cid:…"&gt;</c>).</summary>
internal sealed record GraphInlineImage(string ContentId, string ContentType, byte[] Bytes);

/// <summary>
/// Delivers a queued message to Microsoft 365 via the Graph API.
/// </summary>
internal interface IGraphApiClient
{
    /// <summary>
    /// Parses <paramref name="emlContent"/> and sends it via the Graph API.
    /// Throws on unrecoverable errors; callers should propagate exceptions for retry handling.
    /// </summary>
    /// <param name="emlContent">Raw RFC-5322 bytes from the queue directory.</param>
    /// <param name="senderAddress">SMTP envelope From address – used as the Graph API sender mailbox URL parameter.</param>
    /// <param name="envelopeRecipients">All SMTP RCPT TO addresses (To + Cc + Bcc). Used to reconstruct Bcc recipients
    /// that are stripped from the EML headers by the sending client.</param>
    /// <param name="messageId">Queue message ID – used only for log correlation.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SendAsync(byte[] emlContent, string senderAddress, IReadOnlyList<string> envelopeRecipients, string messageId, CancellationToken ct = default);

    /// <summary>
    /// Sends a simple plain-text notification email directly via the Graph API.
    /// Returns without throwing when Graph API is not configured or unavailable.
    /// </summary>
    Task SendNotificationAsync(string from, IEnumerable<string> to, string subject, string bodyText, CancellationToken ct = default);

    /// <summary>
    /// Sends an HTML notification email (e.g. the periodic operations report) directly via the
    /// Graph API, optionally with a CID inline image. Returns <see langword="true"/> when accepted
    /// by Graph; <see langword="false"/> when Graph is not configured or the send failed (failures
    /// are logged, never thrown).
    /// </summary>
    Task<bool> SendHtmlNotificationAsync(string from, IEnumerable<string> to, string subject, string htmlBody, GraphInlineImage? inlineImage = null, CancellationToken ct = default);

    /// <summary>
    /// Sends a plain-text email with a single inline attachment (e.g. a configuration backup).
    /// Unlike <see cref="SendNotificationAsync"/>, this throws on failure so the caller can react.
    /// </summary>
    Task SendNotificationWithAttachmentAsync(
        string from, IEnumerable<string> to, string subject, string bodyText,
        string attachmentName, byte[] attachmentBytes, string attachmentContentType,
        CancellationToken ct = default);
}
