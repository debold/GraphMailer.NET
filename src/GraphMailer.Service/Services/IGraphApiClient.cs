namespace GraphMailer.Service.Services;

/// <summary>An image embedded in an HTML email as a CID inline attachment (<c>&lt;img src="cid:…"&gt;</c>).</summary>
internal sealed record GraphInlineImage(string ContentId, string ContentType, byte[] Bytes);

/// <summary>How a message was delivered to Graph (metrics/statistics only).</summary>
/// <param name="Variant">"sendMail" (single request) or "draftUpload" (draft + upload session).</param>
/// <param name="AttachmentCount">Total attachments (small + large).</param>
/// <param name="AttachmentBytes">Total attachment payload bytes (decoded).</param>
/// <param name="Relayed">
/// True when the message went out through the relay mailbox because the sender has no
/// mailbox of its own (distribution group, mail-enabled public folder, mail user).
/// </param>
internal sealed record GraphDeliveryResult(string Variant, int AttachmentCount, long AttachmentBytes, bool Relayed = false)
{
    public const string VariantSendMail = "sendMail";
    public const string VariantDraftUpload = "draftUpload";
}

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
    /// <param name="saveToSentItems">Keep a copy in the sender's Sent Items folder. <see langword="true"/> for
    /// mail relayed from an SMTP client (the sender expects to find it there), <see langword="false"/> for
    /// service-generated mail (NDRs, admin notifications) that would only clutter the mailbox.</param>
    /// <param name="relayMailbox">
    /// Mailbox to retry through when <paramref name="senderAddress"/> turns out to have no Exchange
    /// Online mailbox — the case for distribution groups, mail-enabled public folders and mail users.
    /// The retry keeps the original From, which Exchange authorises via the relay mailbox's SendAs
    /// permission. <see langword="null"/> disables the fallback.
    /// </param>
    /// <param name="sendingAsRelay">
    /// <see langword="true"/> when <paramref name="senderAddress"/> is already the relay mailbox
    /// because the router recognised the sender as one without a mailbox of its own. There is then
    /// no fallback left to try, so a SendAs rejection can only mean the missing Exchange
    /// permission and is reported as such instead of being mistaken for a header problem.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Delivery statistics (variant, attachment counts) for the metrics store.</returns>
    Task<GraphDeliveryResult> SendAsync(byte[] emlContent, string senderAddress, IReadOnlyList<string> envelopeRecipients, string messageId, bool saveToSentItems, string? relayMailbox = null, bool sendingAsRelay = false, CancellationToken ct = default);

    /// <summary>
    /// Sends an HTML notification email (admin notifications, the periodic operations report)
    /// directly via the Graph API, optionally with a CID inline image. Returns
    /// <see langword="true"/> when accepted by Graph; <see langword="false"/> when Graph is not
    /// configured or the send failed (failures are logged, never thrown).
    /// </summary>
    Task<bool> SendHtmlNotificationAsync(string from, IEnumerable<string> to, string subject, string htmlBody, GraphInlineImage? inlineImage = null, CancellationToken ct = default);

    /// <summary>
    /// Sends an HTML email with a single inline attachment (e.g. a configuration backup).
    /// Unlike <see cref="SendHtmlNotificationAsync"/>, this throws on failure so the caller can react.
    /// </summary>
    Task SendNotificationWithAttachmentAsync(
        string from, IEnumerable<string> to, string subject, string bodyHtml,
        string attachmentName, byte[] attachmentBytes, string attachmentContentType,
        CancellationToken ct = default);
}
