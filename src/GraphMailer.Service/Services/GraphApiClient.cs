using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.Messages.Item.Attachments.CreateUploadSession;
using Microsoft.Graph.Users.Item.SendMail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using GraphMailer.Service.Configuration;

namespace GraphMailer.Service.Services;

/// <summary>
/// Delivers mail to Microsoft 365 via the Microsoft Graph API.
///
/// Routing:
///   - Messages where all attachments are &lt; 3 MB  →  POST /users/{id}/sendMail  (single request)
///   - Messages with any attachment ≥ 3 MB         →  POST /users/{id}/messages (draft)
///                                                     + upload session per large attachment
///                                                     + POST /messages/{id}/send
///
/// The Graph API /sendMail endpoint accepts a maximum total request body of 4 MB.
/// Base64-encoding inflates binary content by ~33 %, so 3 MB raw ≈ 4 MB on the wire.
/// </summary>
internal sealed class GraphApiClient : IGraphApiClient
{
    // Attachments above this size are uploaded via a dedicated upload session
    private const long LargeAttachmentThreshold = 3 * 1024 * 1024; // 3 MB

    // Graph hard-caps write requests at 4 MB. Base64 inflates attachment bytes by ~4/3;
    // this budget leaves headroom for the JSON envelope, subject/body and recipients.
    internal const long MaxDirectRequestBytes = 3_500_000;

    // Upload-session slices must be multiples of 320 KiB; 12 × 320 KiB ≈ 3.75 MiB
    // stays under the 4 MB per-request cap.
    private const int UploadSliceSize = 320 * 1024 * 12;

    // Exchange Online rejects messages with more envelope recipients than this.
    internal const int MaxRecipients = 500;

    /// <summary>An attachment routed to the draft + upload-session delivery path.</summary>
    internal readonly record struct LargeAttachment(
        string Name, string ContentType, byte[] Content, string? ContentId, bool IsInline);

    private readonly IOptionsMonitor<GraphApiOptions> _options;
    private readonly GraphClientProvider _clientProvider;
    private readonly ILogger<GraphApiClient> _logger;

    public GraphApiClient(
        IOptionsMonitor<GraphApiOptions> options,
        GraphClientProvider clientProvider,
        ILogger<GraphApiClient> logger)
    {
        _options = options;
        _clientProvider = clientProvider;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    public async Task SendAsync(byte[] emlContent, string senderAddress, IReadOnlyList<string> envelopeRecipients, string messageId, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!opts.IsConfigured)
            throw new InvalidOperationException(
                "[GraphApi] Graph API is not configured – cannot deliver message. " +
                "Set TenantId, ClientId and ClientSecret in config/graphmailer.json.");

        // Fail fast on the recipient limit: Graph would reject the message anyway, and
        // without the permanent classification the sender's NDR would be needlessly late.
        if (envelopeRecipients.Count > MaxRecipients)
            throw new GraphDeliveryException(
                $"Message has {envelopeRecipients.Count} envelope recipients — Exchange Online allows at most {MaxRecipients} per message. " +
                "Split the distribution list or use a group address.",
                isPermanent: true);

        var client = GetOrCreateClient(opts);
        var sendAs = senderAddress;

        using var stream = new MemoryStream(emlContent);
        var mime = await MimeMessage.LoadAsync(stream, ct);

        var (smallAttachments, largeAttachments) = CollectAttachments(mime);

        // The 4 MB request cap applies to the TOTAL request, not per attachment:
        // several individually small attachments can still overflow a direct send.
        var moved = RebalanceForRequestCap(
            (mime.HtmlBody ?? mime.TextBody ?? string.Empty).Length, smallAttachments, largeAttachments);
        if (moved > 0)
            _logger.LogDebug(
                "[GraphApi] {MessageId}: moved {Moved} attachment(s) to the upload-session path to stay under the 4 MB request cap",
                messageId, moved);

        _logger.LogDebug(
            "[GraphApi] {MessageId}: {Small} small attachment(s), {Large} large attachment(s)",
            messageId, smallAttachments.Count, largeAttachments.Count);

        try
        {
            if (largeAttachments.Count == 0)
                await SendDirectAsync(client, sendAs, mime, smallAttachments, envelopeRecipients, messageId, ct);
            else
                await SendViaDraftAsync(client, sendAs, mime, smallAttachments, largeAttachments, envelopeRecipients, messageId, ct);
        }
        catch (ODataError ex)
        {
            // Extract the structured fields Microsoft Graph returns on every error response.
            // These are lost when the exception propagates as-is because QueueProcessor only
            // stores ex.Message in the metrics DB and the failed-mail metadata.
            var code   = ex.Error?.Code    ?? "UnknownError";
            var msg    = ex.Error?.Message ?? ex.Message;
            var reqId  = ex.Error?.InnerError?.RequestId ?? "n/a";
            var status = ex.ResponseStatusCode;
            var isPermanent = IsPermanentRejection(status, code);

            _logger.LogWarning(
                "[GraphApi] {MessageId} rejected – HTTP {HttpStatus} {ErrorCode}: {ErrorMessage} (RequestId: {RequestId}, permanent: {Permanent})",
                messageId, status, code, msg, reqId, isPermanent);

            throw new GraphDeliveryException(
                $"HTTP {status} {code}: \"{msg}\" (RequestId: {reqId})", isPermanent, ex);
        }
        catch (AuthenticationFailedException ex)
        {
            // Auth failures are always configuration errors (wrong secret, expired cert,
            // tenant/clientId mismatch) and require operator action – log at Error.
            _logger.LogError(ex,
                "[GraphApi] {MessageId} – authentication failed: {Detail}",
                messageId, ex.Message);

            throw new InvalidOperationException(
                $"Authentication failed – cannot obtain Graph API access token. " +
                $"Check TenantId, ClientId and credentials in config. Detail: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Classifies a Graph rejection as permanent (retrying the same message can never
    /// succeed) or transient. Conservative by design: only unambiguous cases are
    /// permanent — auth/permission problems (401/403) stay transient because an
    /// operator can fix them within the retry window.
    /// </summary>
    internal static bool IsPermanentRejection(int status, string code)
    {
        // Error codes that are message-permanent regardless of the HTTP status.
        if (code.Equals("ErrorInvalidRecipients", StringComparison.OrdinalIgnoreCase)
            || code.Equals("ErrorRecipientLimitExceeded", StringComparison.OrdinalIgnoreCase)
            || code.Equals("ErrorMessageSizeExceeded", StringComparison.OrdinalIgnoreCase)
            || code.Equals("MailboxNotEnabledForRESTAPI", StringComparison.OrdinalIgnoreCase)
            || code.Equals("ErrorInvalidUser", StringComparison.OrdinalIgnoreCase))
            return true;

        // 400 Bad Request: the request itself is malformed for Graph — resending the
        //     identical payload cannot succeed.
        // 404 Not Found:   the sender mailbox/user does not exist.
        // 413 Payload Too Large: exceeds Graph's hard 4 MB request cap.
        return status is 400 or 404 or 413;
    }

    // -------------------------------------------------------------------------
    // GraphServiceClient lifecycle — client creation and certificate loading
    // live in GraphClientProvider (shared with GraphDirectoryGateway).
    // -------------------------------------------------------------------------

    private GraphServiceClient GetOrCreateClient(GraphApiOptions opts) =>
        _clientProvider.GetClient(opts);

    // -------------------------------------------------------------------------
    // Delivery paths
    // -------------------------------------------------------------------------

    /// <summary>Single-request delivery for messages with small or no attachments.</summary>
    private async Task SendDirectAsync(
        GraphServiceClient client,
        string sendAs,
        MimeMessage mime,
        List<FileAttachment> attachments,
        IReadOnlyList<string> envelopeRecipients,
        string messageId,
        CancellationToken ct)
    {
        var requestBody = new SendMailPostRequestBody
        {
            Message = BuildMessage(mime, attachments, envelopeRecipients),
            SaveToSentItems = false
        };

        await client.Users[sendAs].SendMail.PostAsync(requestBody, cancellationToken: ct);

        _logger.LogInformation("[GraphApi] Delivered {MessageId} via sendMail (from: {From})",
            messageId, mime.From.ToString());
    }

    /// <summary>
    /// Draft + upload-session delivery for messages with large attachments.
    /// Creates a draft, uploads each large attachment via a dedicated upload session,
    /// then sends the draft. Deletes the draft if anything fails.
    /// </summary>
    private async Task SendViaDraftAsync(
        GraphServiceClient client,
        string sendAs,
        MimeMessage mime,
        List<FileAttachment> smallAttachments,
        List<LargeAttachment> largeAttachments,
        IReadOnlyList<string> envelopeRecipients,
        string messageId,
        CancellationToken ct)
    {
        var draft = await client.Users[sendAs].Messages.PostAsync(
            BuildMessage(mime, smallAttachments, envelopeRecipients), cancellationToken: ct);

        if (draft?.Id is null)
            throw new InvalidOperationException("[GraphApi] Graph API did not return a draft ID.");

        try
        {
            foreach (var attachment in largeAttachments)
            {
                _logger.LogDebug("[GraphApi] Uploading '{Name}' ({Size:N0} bytes) for {MessageId}",
                    attachment.Name, attachment.Content.Length, messageId);

                await UploadLargeAttachmentAsync(client, sendAs, draft.Id, attachment, ct);
            }

            await client.Users[sendAs].Messages[draft.Id].Send.PostAsync(cancellationToken: ct);

            _logger.LogInformation(
                "[GraphApi] Delivered {MessageId} via draft + upload session (from: {From})",
                messageId, mime.From.ToString());
        }
        catch
        {
            // Best-effort draft cleanup so the mailbox doesn't accumulate orphaned drafts
            try
            {
                await client.Users[sendAs].Messages[draft.Id].DeleteAsync(
                    cancellationToken: CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[GraphApi] Could not delete orphaned draft {DraftId} after {MessageId} failed",
                    draft.Id, messageId);
            }

            throw;
        }
    }

    // -------------------------------------------------------------------------
    // Large attachment upload
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates an upload session for a single attachment and uploads it via the SDK's
    /// <see cref="LargeFileUploadTask{T}"/>: slices go through the Graph middleware
    /// pipeline (429/Retry-After and 5xx retries included), and an interrupted upload
    /// is resumed once at the server's NextExpectedRanges instead of restarting from
    /// byte 0.
    /// </summary>
    private async Task UploadLargeAttachmentAsync(
        GraphServiceClient client,
        string sendAs,
        string draftId,
        LargeAttachment attachment,
        CancellationToken ct)
    {
        var (name, contentType, content, contentId, isInline) = attachment;

        var uploadSession = await client.Users[sendAs].Messages[draftId]
            .Attachments.CreateUploadSession.PostAsync(
                new CreateUploadSessionPostRequestBody
                {
                    AttachmentItem = new AttachmentItem
                    {
                        AttachmentType = AttachmentType.File,
                        Name = name,
                        Size = content.Length,
                        ContentType = contentType,
                        ContentId = contentId,
                        IsInline = isInline,
                    }
                },
                cancellationToken: ct);

        if (uploadSession?.UploadUrl is null)
            throw new InvalidOperationException(
                $"[GraphApi] Graph API returned no upload URL for attachment '{name}'.");

        using var contentStream = new MemoryStream(content);
        var uploadTask = new LargeFileUploadTask<FileAttachment>(
            uploadSession, contentStream, UploadSliceSize, client.RequestAdapter);

        UploadResult<FileAttachment> result;
        try
        {
            result = await uploadTask.UploadAsync(cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "[GraphApi] Upload of attachment '{Name}' was interrupted — resuming at the last confirmed range",
                name);
            result = await uploadTask.ResumeAsync(cancellationToken: ct);
        }

        if (!result.UploadSucceeded)
            throw new InvalidOperationException(
                $"[GraphApi] Upload session for attachment '{name}' did not complete.");
    }

    // -------------------------------------------------------------------------
    // Message / attachment builders
    // -------------------------------------------------------------------------

    internal static Message BuildMessage(MimeMessage mime, List<FileAttachment> attachments, IReadOnlyList<string> envelopeRecipients)
    {
        // Derive BCC recipients: envelope has all RCPT TO addresses (To + Cc + Bcc).
        // Clients strip the Bcc header before sending, so mime.Bcc is always empty.
        // BCC = envelope addresses that are NOT in the To: or Cc: headers.
        var explicitAddresses = new HashSet<string>(
            mime.To.Mailboxes.Concat(mime.Cc.Mailboxes).Select(m => m.Address),
            StringComparer.OrdinalIgnoreCase);

        var bccRecipients = envelopeRecipients
            .Where(addr => !explicitAddresses.Contains(addr))
            .Select(addr => new Recipient { EmailAddress = new EmailAddress { Address = addr } })
            .ToList();

        var message = new Message
        {
            Subject = mime.Subject,
            Body = new ItemBody
            {
                ContentType = mime.HtmlBody is not null ? BodyType.Html : BodyType.Text,
                Content = mime.HtmlBody ?? mime.TextBody ?? string.Empty
            },
            From = ConvertMailbox(mime.From.Mailboxes.FirstOrDefault()),
            ReplyTo = ConvertAddressList(mime.ReplyTo),
            ToRecipients = ConvertAddressList(mime.To),
            CcRecipients = ConvertAddressList(mime.Cc),
            BccRecipients = bccRecipients,
            Attachments = attachments.ConvertAll(a => (Attachment)a),
            Importance = MapImportance(mime),
        };

        // Original Message-ID: preserved so replies and threading on the recipient
        // side keep referencing the relayed message. Graph accepts it at creation.
        if (!string.IsNullOrWhiteSpace(mime.MessageId))
            message.InternetMessageId = $"<{mime.MessageId}>";

        // Custom headers: Graph only permits custom internet headers whose name starts
        // with "x-". Transport-reserved x-ms-exchange-* headers are skipped, and
        // x-priority is skipped because it is already mapped to Importance above.
        var xHeaders = mime.Headers
            .Where(h => h.Field.StartsWith("x-", StringComparison.OrdinalIgnoreCase)
                     && !h.Field.StartsWith("x-ms-exchange", StringComparison.OrdinalIgnoreCase)
                     && !h.Field.Equals("x-priority", StringComparison.OrdinalIgnoreCase))
            .Select(h => new InternetMessageHeader { Name = h.Field, Value = h.Value })
            .ToList();
        if (xHeaders.Count > 0)
            message.InternetMessageHeaders = xHeaders;

        // Threading: In-Reply-To / References cannot be set as internet headers (no
        // x- prefix) — Graph exposes them through the underlying MAPI properties
        // PidTagInReplyToId (0x1042) and PidTagInternetReferences (0x1039).
        var extended = new List<SingleValueLegacyExtendedProperty>();
        if (!string.IsNullOrWhiteSpace(mime.InReplyTo))
            extended.Add(new() { Id = "String 0x1042", Value = $"<{mime.InReplyTo}>" });
        if (mime.References.Count > 0)
            extended.Add(new() { Id = "String 0x1039", Value = string.Join(" ", mime.References.Select(r => $"<{r}>")) });
        if (extended.Count > 0)
            message.SingleValueExtendedProperties = extended;

        return message;
    }

    /// <summary>
    /// Maps the MIME priority signals to Graph's Importance. The Importance header wins;
    /// X-Priority (mapped separately by many legacy senders) is the fallback. Returns
    /// null for normal priority so the property is simply omitted.
    /// </summary>
    private static Importance? MapImportance(MimeMessage mime) => mime.Importance switch
    {
        MessageImportance.High => Importance.High,
        MessageImportance.Low => Importance.Low,
        _ => mime.XPriority switch
        {
            XMessagePriority.Highest or XMessagePriority.High => Importance.High,
            XMessagePriority.Lowest or XMessagePriority.Low => Importance.Low,
            _ => null,
        },
    };

    /// <summary>
    /// The 4 MB Graph request cap applies to the whole request, not to a single
    /// attachment. When the body plus the inline (small) attachments would exceed the
    /// budget, the largest small attachments are moved to the upload-session path until
    /// the direct payload fits — otherwise a message whose attachments are individually
    /// small but collectively large is rejected with HTTP 413 and never deliverable.
    /// Returns the number of attachments moved.
    /// </summary>
    internal static int RebalanceForRequestCap(
        long bodyLength,
        List<FileAttachment> small,
        List<LargeAttachment> large)
    {
        long Estimate() => bodyLength + small.Sum(a => (long)(a.ContentBytes?.Length ?? 0) * 4 / 3);

        var moved = 0;
        while (small.Count > 0 && Estimate() > MaxDirectRequestBytes)
        {
            var biggest = small.MaxBy(a => a.ContentBytes?.Length ?? 0)!;
            small.Remove(biggest);
            large.Add(new LargeAttachment(
                biggest.Name ?? "attachment",
                biggest.ContentType ?? "application/octet-stream",
                biggest.ContentBytes ?? [],
                biggest.ContentId,
                biggest.IsInline ?? false));
            moved++;
        }
        return moved;
    }

    /// <summary>
    /// Splits message attachments into small (&lt; 3 MB) and large (≥ 3 MB) lists.
    /// Inline parts (embedded images etc.) keep their ContentId and IsInline flag so
    /// <c>&lt;img src="cid:…"&gt;</c> references in the HTML body keep working — without
    /// them the image shows up as a visible attachment instead.
    /// </summary>
    internal static (List<FileAttachment> Small, List<LargeAttachment> Large)
        CollectAttachments(MimeMessage message)
    {
        var small = new List<FileAttachment>();
        var large = new List<LargeAttachment>();

        // Include both Attachments and inline MIME parts so embedded images are not dropped
        var parts = message.BodyParts.OfType<MimePart>()
            .Where(p => (p.IsAttachment || p.ContentDisposition?.Disposition == ContentDisposition.Inline)
                     && p.ContentType.MimeType != "text/plain"
                     && p.ContentType.MimeType != "text/html");

        foreach (var part in parts)
        {
            var name = part.FileName ?? "attachment";
            var mediaType = part.ContentType.MimeType;
            var isInline = part.ContentDisposition?.Disposition == ContentDisposition.Inline;
            var contentId = string.IsNullOrWhiteSpace(part.ContentId) ? null : part.ContentId;

            using var ms = new MemoryStream();
            part.Content?.DecodeTo(ms);
            var bytes = ms.ToArray();

            if (bytes.Length <= LargeAttachmentThreshold)
                small.Add(new FileAttachment
                {
                    Name = name,
                    ContentBytes = bytes,
                    ContentType = mediaType,
                    ContentId = contentId,
                    IsInline = isInline,
                });
            else
                large.Add(new LargeAttachment(name, mediaType, bytes, contentId, isInline));
        }

        return (small, large);
    }

    private static List<Recipient> ConvertAddressList(InternetAddressList list)
    {
        return list.Mailboxes
            .Select(m => new Recipient { EmailAddress = new EmailAddress { Address = m.Address, Name = m.Name } })
            .ToList();
    }

    private static Recipient? ConvertMailbox(MailboxAddress? mailbox) =>
        mailbox is null
            ? null
            : new Recipient { EmailAddress = new EmailAddress { Address = mailbox.Address, Name = mailbox.Name } };

    // -------------------------------------------------------------------------
    // Notification helper (used by AdminNotificationService)
    // -------------------------------------------------------------------------

    public async Task SendNotificationAsync(
        string from,
        IEnumerable<string> to,
        string subject,
        string bodyText,
        CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!opts.IsConfigured)
        {
            _logger.LogDebug("[GraphApi] SendNotification skipped – Graph API not configured");
            return;
        }

        try
        {
            var client = GetOrCreateClient(opts);
            var message = new Message
            {
                Subject = subject,
                Body = new ItemBody { ContentType = BodyType.Text, Content = bodyText },
                ToRecipients = to.Select(addr =>
                    new Recipient { EmailAddress = new EmailAddress { Address = addr } }).ToList()
            };

            await client.Users[from].SendMail.PostAsync(
                new SendMailPostRequestBody { Message = message, SaveToSentItems = false },
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            // Notifications must never crash the caller
            _logger.LogWarning(ex, "[GraphApi] Failed to send admin notification: {Subject}", subject);
        }
    }

    public async Task<bool> SendHtmlNotificationAsync(
        string from,
        IEnumerable<string> to,
        string subject,
        string htmlBody,
        GraphInlineImage? inlineImage = null,
        CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!opts.IsConfigured)
        {
            _logger.LogDebug("[GraphApi] SendHtmlNotification skipped – Graph API not configured");
            return false;
        }

        try
        {
            var client = GetOrCreateClient(opts);
            var message = new Message
            {
                Subject = subject,
                Body = new ItemBody { ContentType = BodyType.Html, Content = htmlBody },
                ToRecipients = to.Select(addr =>
                    new Recipient { EmailAddress = new EmailAddress { Address = addr } }).ToList()
            };

            if (inlineImage is not null)
            {
                // CID inline image: ContentId matches the <img src="cid:…"> in the HTML body;
                // IsInline keeps it out of the visible attachment list.
                message.Attachments =
                [
                    new FileAttachment
                    {
                        Name = "chart.png",
                        ContentType = inlineImage.ContentType,
                        ContentBytes = inlineImage.Bytes,
                        ContentId = inlineImage.ContentId,
                        IsInline = true,
                    }
                ];
            }

            await client.Users[from].SendMail.PostAsync(
                new SendMailPostRequestBody { Message = message, SaveToSentItems = false },
                cancellationToken: ct);
            return true;
        }
        catch (Exception ex)
        {
            // Reports must never crash the scheduler
            _logger.LogWarning(ex, "[GraphApi] Failed to send HTML notification: {Subject}", subject);
            return false;
        }
    }

    public async Task SendNotificationWithAttachmentAsync(
        string from,
        IEnumerable<string> to,
        string subject,
        string bodyText,
        string attachmentName,
        byte[] attachmentBytes,
        string attachmentContentType,
        CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!opts.IsConfigured)
            throw new InvalidOperationException("Graph API is not configured; cannot send the email.");

        // Inline attachment via sendMail — fine for the small backup files (< 3 MB request cap).
        var client = GetOrCreateClient(opts);
        var message = new Message
        {
            Subject = subject,
            Body = new ItemBody { ContentType = BodyType.Text, Content = bodyText },
            ToRecipients = to.Select(addr =>
                new Recipient { EmailAddress = new EmailAddress { Address = addr } }).ToList(),
            Attachments =
            [
                new FileAttachment
                {
                    Name = attachmentName,
                    ContentType = attachmentContentType,
                    ContentBytes = attachmentBytes,
                }
            ],
        };

        await client.Users[from].SendMail.PostAsync(
            new SendMailPostRequestBody { Message = message, SaveToSentItems = false },
            cancellationToken: ct);
    }
}
