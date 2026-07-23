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

    // Graph rejects a message carrying more custom internet headers than this with
    // 400 InvalidInternetMessageHeaderCollection ("Maximum number of headers in one
    // message should be less than or equal to 5"). Exchange limits it because every
    // custom header burns a named property in the store ("X-haustion"). Real-world mail
    // routinely carries more (X-Mailer, X-Originating-IP, X-Spam-*, X-Virus-Scanned, …),
    // so the list must be capped here — sending more can never succeed.
    internal const int MaxCustomHeaders = 5;

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

    public async Task<GraphDeliveryResult> SendAsync(byte[] emlContent, string senderAddress, IReadOnlyList<string> envelopeRecipients, string messageId, bool saveToSentItems, CancellationToken ct = default)
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

        var split = MimeMessageSplitter.Split(mime);
        if (split.DroppedAlternativeBodies > 0)
            _logger.LogDebug(
                "[GraphApi] {MessageId}: {Count} surplus multipart/alternative body rendering(s) not carried over (clients would not display them either)",
                messageId, split.DroppedAlternativeBodies);

        WarnAboutUnrelayableContent(mime, split, messageId);

        // Policy rejection with the concrete reason: these addresses stand in the To:/Cc:
        // header but were never RCPT TO'd, so they are not delivered to.
        var notInEnvelope = FindNonEnvelopeRecipients(mime, envelopeRecipients);
        if (notInEnvelope.Count > 0)
            _logger.LogWarning(
                "[GraphApi] {MessageId}: {Count} header recipient(s) not in the SMTP envelope — not delivered to: {Recipients}",
                messageId, notInEnvelope.Count, string.Join(", ", notInEnvelope));

        var (smallAttachments, largeAttachments) = CollectAttachments(split);

        // The 4 MB request cap applies to the TOTAL request, not per attachment:
        // several individually small attachments can still overflow a direct send.
        var moved = RebalanceForRequestCap(
            EstimateBodyBytes(split), smallAttachments, largeAttachments);
        if (moved > 0)
            _logger.LogDebug(
                "[GraphApi] {MessageId}: moved {Moved} attachment(s) to the upload-session path to stay under the 4 MB request cap",
                messageId, moved);

        _logger.LogDebug(
            "[GraphApi] {MessageId}: {Small} small attachment(s), {Large} large attachment(s)",
            messageId, smallAttachments.Count, largeAttachments.Count);

        var attachmentBytes =
            smallAttachments.Sum(a => (long)(a.ContentBytes?.Length ?? 0)) +
            largeAttachments.Sum(a => (long)a.Content.Length);
        var attachmentCount = smallAttachments.Count + largeAttachments.Count;

        try
        {
            var variant = await DeliverWithFallbacksAsync(
                client, sendAs, mime, smallAttachments, largeAttachments,
                envelopeRecipients, messageId, saveToSentItems, ct);

            return new GraphDeliveryResult(variant, attachmentCount, attachmentBytes);
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
    /// Runs the delivery, degrading rather than failing when Graph objects to something
    /// that is not the message itself. Two rejections are recoverable and are retried once:
    ///
    ///   1. The optional fidelity extras (custom headers, <c>Sender</c>) — resent without
    ///      them. A mail must never be lost over an <c>X-Spam-Level</c> header.
    ///   2. Request too large on the direct path — resent through the draft + upload
    ///      session, which moves the attachment bytes out of the request body. Only a
    ///      message whose *body* alone busts the cap is genuinely undeliverable.
    ///
    /// Returns the <see cref="GraphDeliveryResult"/> variant that actually delivered.
    /// </summary>
    private async Task<string> DeliverWithFallbacksAsync(
        GraphServiceClient client,
        string sendAs,
        MimeMessage mime,
        List<FileAttachment> smallAttachments,
        List<LargeAttachment> largeAttachments,
        IReadOnlyList<string> envelopeRecipients,
        string messageId,
        bool saveToSentItems,
        CancellationToken ct)
    {
        try
        {
            return await DeliverAsync(
                client, sendAs, mime, smallAttachments, largeAttachments,
                envelopeRecipients, messageId, saveToSentItems, MessageFidelity.Full, ct);
        }
        catch (ODataError ex) when (IsOptionalPropertyRejection(ex.Error?.Code))
        {
            // The message is fine — Graph objects to the extras we added for fidelity.
            // Warning with the concrete cause: the operator must be able to see that the
            // delivered mail is missing headers the sender set.
            _logger.LogWarning(
                "[GraphApi] {MessageId}: Graph rejected the preserved headers/sender ({ErrorCode}) — " +
                "resending without custom headers and the Sender header so the message still arrives",
                messageId, ex.Error?.Code);

            return await DeliverAsync(
                client, sendAs, mime, smallAttachments, largeAttachments,
                envelopeRecipients, messageId, saveToSentItems, MessageFidelity.WithoutOptionalExtras, ct);
        }
        catch (ODataError ex) when (IsRequestTooLarge(ex) && smallAttachments.Count > 0)
        {
            // Our size estimate was too optimistic. Rather than NDR a message that is
            // deliverable, move every remaining attachment onto the upload-session path —
            // that leaves only the body in the request.
            _logger.LogWarning(
                "[GraphApi] {MessageId}: direct send exceeded Graph's request cap ({ErrorCode}) — " +
                "retrying via draft + upload session with all {Count} attachment(s) uploaded separately",
                messageId, ex.Error?.Code, smallAttachments.Count);

            foreach (var attachment in smallAttachments)
                largeAttachments.Add(new LargeAttachment(
                    attachment.Name ?? "attachment",
                    attachment.ContentType ?? "application/octet-stream",
                    attachment.ContentBytes ?? [],
                    attachment.ContentId,
                    attachment.IsInline ?? false));
            smallAttachments.Clear();

            return await DeliverAsync(
                client, sendAs, mime, smallAttachments, largeAttachments,
                envelopeRecipients, messageId, saveToSentItems, MessageFidelity.Full, ct);
        }
    }

    private async Task<string> DeliverAsync(
        GraphServiceClient client,
        string sendAs,
        MimeMessage mime,
        List<FileAttachment> smallAttachments,
        List<LargeAttachment> largeAttachments,
        IReadOnlyList<string> envelopeRecipients,
        string messageId,
        bool saveToSentItems,
        MessageFidelity fidelity,
        CancellationToken ct)
    {
        if (largeAttachments.Count == 0)
        {
            await SendDirectAsync(client, sendAs, mime, smallAttachments, envelopeRecipients,
                messageId, saveToSentItems, fidelity, ct);
            return GraphDeliveryResult.VariantSendMail;
        }

        await SendViaDraftAsync(client, sendAs, mime, smallAttachments, largeAttachments,
            envelopeRecipients, messageId, fidelity, ct);
        return GraphDeliveryResult.VariantDraftUpload;
    }

    /// <summary>
    /// Graph rejections that mean "the message is fine, one of the optional fidelity
    /// extras is not": too many custom internet headers, or a <c>Sender</c> the mailbox
    /// is not allowed to send as. Both are recoverable by dropping the extras.
    /// </summary>
    internal static bool IsOptionalPropertyRejection(string? code) =>
        code is not null
        && (code.Equals("InvalidInternetMessageHeaderCollection", StringComparison.OrdinalIgnoreCase)
            || code.Equals("ErrorSendAsDenied", StringComparison.OrdinalIgnoreCase)
            || code.Equals("ErrorInvalidSender", StringComparison.OrdinalIgnoreCase));

    private static bool IsRequestTooLarge(ODataError ex) =>
        ex.ResponseStatusCode == 413
        || (ex.Error?.Code?.Equals("ErrorMessageSizeExceeded", StringComparison.OrdinalIgnoreCase) ?? false);

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

        // Never permanent: these are recoverable by resending without the optional
        // fidelity extras (see DeliverWithFallbacksAsync). Reaching the classifier at all
        // means even the stripped-down retry failed — treat it as transient so the message
        // keeps its retry window instead of being NDR'd over a header.
        if (IsOptionalPropertyRejection(code))
            return false;

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
        bool saveToSentItems,
        MessageFidelity fidelity,
        CancellationToken ct)
    {
        var requestBody = new SendMailPostRequestBody
        {
            Message = BuildMessage(mime, attachments, envelopeRecipients, fidelity),
            SaveToSentItems = saveToSentItems
        };

        await client.Users[sendAs].SendMail.PostAsync(requestBody, cancellationToken: ct);

        // Attachment count at Information level: operators must be able to see from the
        // default log alone whether a delivered message carried its attachments.
        _logger.LogInformation("[GraphApi] Delivered {MessageId} via sendMail (from: {From}, attachments: {AttachmentCount})",
            messageId, mime.From.ToString(), attachments.Count);
    }

    /// <summary>
    /// Draft + upload-session delivery for messages with large attachments.
    /// Creates a draft, uploads each large attachment via a dedicated upload session,
    /// then sends the draft. Deletes the draft if anything fails.
    ///
    /// This path always leaves a copy in Sent Items: POST /messages/{id}/send has no
    /// saveToSentItems switch. That matches what relayed SMTP mail wants anyway, and
    /// service-generated mail (NDRs, notifications) never carries large attachments.
    /// </summary>
    private async Task SendViaDraftAsync(
        GraphServiceClient client,
        string sendAs,
        MimeMessage mime,
        List<FileAttachment> smallAttachments,
        List<LargeAttachment> largeAttachments,
        IReadOnlyList<string> envelopeRecipients,
        string messageId,
        MessageFidelity fidelity,
        CancellationToken ct)
    {
        var draft = await client.Users[sendAs].Messages.PostAsync(
            BuildMessage(mime, smallAttachments, envelopeRecipients, fidelity), cancellationToken: ct);

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
                "[GraphApi] Delivered {MessageId} via draft + upload session (from: {From}, attachments: {AttachmentCount})",
                messageId, mime.From.ToString(), smallAttachments.Count + largeAttachments.Count);
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

    /// <summary>How much of the original message is carried over.</summary>
    internal enum MessageFidelity
    {
        /// <summary>Everything Graph accepts, including custom headers and Sender.</summary>
        Full,
        /// <summary>
        /// Without the extras Graph may reject (custom internet headers, Sender). Used for
        /// the one retry after such a rejection — delivering the mail beats preserving a header.
        /// </summary>
        WithoutOptionalExtras,
    }

    internal static Message BuildMessage(
        MimeMessage mime,
        List<FileAttachment> attachments,
        IReadOnlyList<string> envelopeRecipients,
        MessageFidelity fidelity = MessageFidelity.Full)
    {
        // Delivery follows the SMTP envelope, never the headers. The envelope holds every
        // RCPT TO address (To + Cc + Bcc); a To:/Cc: header may list addresses the client
        // never sent RCPT TO for (per-domain splitting, selective relaying). Handing those
        // to Graph would make the relay invent recipients, so header addresses are kept
        // only where the envelope confirms them — and every envelope address still lands in
        // exactly one of To/Cc/Bcc, so no recipient is lost either.
        var envelope = new HashSet<string>(envelopeRecipients, StringComparer.OrdinalIgnoreCase);

        // BCC = envelope addresses in neither header. Clients strip the Bcc header before
        // sending, so mime.Bcc is always empty and BCC has to be derived.
        var headerAddresses = new HashSet<string>(
            mime.To.Mailboxes.Concat(mime.Cc.Mailboxes).Select(m => m.Address),
            StringComparer.OrdinalIgnoreCase);

        var bccRecipients = envelopeRecipients
            .Where(addr => !headerAddresses.Contains(addr))
            .Select(addr => new Recipient { EmailAddress = new EmailAddress { Address = addr } })
            .ToList();

        // Body selection must use the same classification as CollectAttachments —
        // otherwise a part could end up in both places (duplicated) or neither (lost).
        // MimeKit's HtmlBody/TextBody use a stricter attachment notion and disagree
        // with the splitter for e.g. named text parts.
        var split = MimeMessageSplitter.Split(mime);

        var message = new Message
        {
            Subject = mime.Subject,
            Body = new ItemBody
            {
                ContentType = split.HtmlBody is not null ? BodyType.Html : BodyType.Text,
                Content = split.HtmlBody?.Text ?? split.TextBody?.Text ?? string.Empty
            },
            From = ConvertMailbox(mime.From.Mailboxes.FirstOrDefault()),
            ReplyTo = ConvertAddressList(mime.ReplyTo),
            ToRecipients = ConvertAddressList(mime.To, envelope),
            CcRecipients = ConvertAddressList(mime.Cc, envelope),
            BccRecipients = bccRecipients,
            Attachments = attachments.ConvertAll(a => (Attachment)a),
            Importance = MapImportance(mime),
        };

        // Receipt requests: the sender asked to be told when the mail is delivered/read.
        // Graph carries only the boolean — the receipt goes to the message sender, not to
        // the address named in the header, so the routing is an approximation.
        if (mime.Headers.Contains(HeaderId.DispositionNotificationTo)
            || mime.Headers.Contains("X-Confirm-Reading-To"))
            message.IsReadReceiptRequested = true;
        if (mime.Headers.Contains(HeaderId.ReturnReceiptTo))
            message.IsDeliveryReceiptRequested = true;

        // Sender differs from From for shared mailboxes and "on behalf of" senders.
        // Exchange may refuse a Sender the mailbox cannot send as — that rejection is
        // caught and retried without it (see DeliverWithFallbacksAsync).
        if (fidelity == MessageFidelity.Full && mime.Sender is not null)
            message.Sender = ConvertMailbox(mime.Sender);

        // Original Message-ID: preserved so replies and threading on the recipient
        // side keep referencing the relayed message. Graph accepts it at creation.
        if (!string.IsNullOrWhiteSpace(mime.MessageId))
            message.InternetMessageId = $"<{mime.MessageId}>";

        if (fidelity == MessageFidelity.Full)
        {
            var xHeaders = CollectCustomHeaders(mime);
            if (xHeaders.Count > 0)
                message.InternetMessageHeaders = xHeaders;
        }

        // Properties without a first-class Graph equivalent, carried as the underlying
        // MAPI properties — the same mechanism Outlook itself stores them in.
        var extended = new List<SingleValueLegacyExtendedProperty>();

        // Threading: In-Reply-To / References cannot be set as internet headers (no
        // x- prefix) — PidTagInReplyToId (0x1042) / PidTagInternetReferences (0x1039).
        if (!string.IsNullOrWhiteSpace(mime.InReplyTo))
            extended.Add(new() { Id = "String 0x1042", Value = $"<{mime.InReplyTo}>" });
        if (mime.References.Count > 0)
            extended.Add(new() { Id = "String 0x1039", Value = string.Join(" ", mime.References.Select(r => $"<{r}>")) });

        // Private/confidential marking: Graph's message resource has no sensitivity
        // property (only event does), so PidTagSensitivity (0x0036) is the only way to
        // keep it. Without this the marking is silently lost on every relayed mail.
        if (MapSensitivity(mime) is { } sensitivity)
            extended.Add(new() { Id = "Integer 0x0036", Value = sensitivity.ToString() });

        if (extended.Count > 0)
            message.SingleValueExtendedProperties = extended;

        return message;
    }

    /// <summary>
    /// Custom internet headers to carry over, capped at Graph's hard limit of
    /// <see cref="MaxCustomHeaders"/>. Graph only permits names starting with "x-";
    /// transport-reserved x-ms-exchange-* headers are skipped, and x-priority is skipped
    /// because it is already mapped to Importance.
    /// </summary>
    internal static List<InternetMessageHeader> CollectCustomHeaders(MimeMessage mime) =>
        mime.Headers
            .Where(h => h.Field.StartsWith("x-", StringComparison.OrdinalIgnoreCase)
                     && !h.Field.StartsWith("x-ms-exchange", StringComparison.OrdinalIgnoreCase)
                     && !h.Field.Equals("x-priority", StringComparison.OrdinalIgnoreCase))
            .Take(MaxCustomHeaders)
            .Select(h => new InternetMessageHeader { Name = h.Field, Value = h.Value })
            .ToList();

    /// <summary>
    /// Maps the MIME priority signals to Graph's Importance. The Importance header wins;
    /// X-Priority (used by many legacy senders) and the RFC 2156 Priority header are the
    /// fallbacks. Returns null for normal priority so the property is simply omitted.
    /// </summary>
    private static Importance? MapImportance(MimeMessage mime) => mime.Importance switch
    {
        MessageImportance.High => Importance.High,
        MessageImportance.Low => Importance.Low,
        _ => mime.XPriority switch
        {
            XMessagePriority.Highest or XMessagePriority.High => Importance.High,
            XMessagePriority.Lowest or XMessagePriority.Low => Importance.Low,
            _ => mime.Priority switch
            {
                MessagePriority.Urgent => Importance.High,
                MessagePriority.NonUrgent => Importance.Low,
                _ => null,
            },
        },
    };

    /// <summary>
    /// RFC 2156 <c>Sensitivity:</c> header → PidTagSensitivity value
    /// (0 Normal · 1 Personal · 2 Private · 3 Company-Confidential). Returns null for
    /// Normal, absent or unknown tokens so the property is omitted (Normal is the default).
    /// </summary>
    internal static int? MapSensitivity(MimeMessage mime)
    {
        var value = mime.Headers[HeaderId.Sensitivity]?.Trim();
        if (string.IsNullOrEmpty(value)) return null;

        if (value.Equals("Personal", StringComparison.OrdinalIgnoreCase)) return 1;
        if (value.Equals("Private", StringComparison.OrdinalIgnoreCase)) return 2;
        if (value.Equals("Company-Confidential", StringComparison.OrdinalIgnoreCase)) return 3;
        return null;
    }

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
        long Estimate() => bodyLength + small.Sum(a => Base64Length(a.ContentBytes?.Length ?? 0));

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

    /// <summary>Exact base64 length of <paramref name="rawBytes"/> raw bytes, padding included.</summary>
    internal static long Base64Length(long rawBytes) => (rawBytes + 2) / 3 * 4;

    /// <summary>
    /// Size the message body contributes to the request, in bytes on the wire: UTF-8 rather
    /// than UTF-16 characters (the old estimate compared char counts against a byte budget).
    /// Deliberately not padded with a safety factor — <see cref="MaxDirectRequestBytes"/>
    /// already keeps 500 KB clear of Graph's 4 MB cap, and an over-cautious estimate would
    /// push messages onto the slower upload path that the direct send handles fine. A real
    /// 413 is recovered by retrying via the upload session (see DeliverWithFallbacksAsync).
    /// </summary>
    internal static long EstimateBodyBytes(MimeMessageSplitter.SplitResult split)
    {
        var body = split.HtmlBody?.Text ?? split.TextBody?.Text;
        return string.IsNullOrEmpty(body) ? 0 : System.Text.Encoding.UTF8.GetByteCount(body);
    }

    /// <summary>
    /// Logs the parts of a message that cannot survive the MIME → Graph translation, so the
    /// degradation is visible instead of silent. S/MIME and PGP are a Warning: the relay
    /// rebuilds the message, which invalidates the signature the sender relied on.
    /// </summary>
    private void WarnAboutUnrelayableContent(
        MimeMessage mime, MimeMessageSplitter.SplitResult split, string messageId)
    {
        if (mime.Body is MimeKit.Cryptography.MultipartSigned or MimeKit.Cryptography.MultipartEncrypted
            || (mime.Body?.ContentType.IsMimeType("application", "pkcs7-mime") ?? false))
            _logger.LogWarning(
                "[GraphApi] {MessageId} is S/MIME/PGP protected — Graph delivery rebuilds the message, " +
                "so the signature will not verify at the recipient (encrypted parts arrive as an attachment)",
                messageId);

        // A Graph message body is single-typed: with both renderings present the HTML one
        // wins and the plain-text alternative cannot come along. Debug rather than Warning
        // on purpose — this is the normal shape of almost every HTML mail, and Exchange
        // regenerates a text version, so warning here would drown the real warnings.
        if (split.HtmlBody is not null && split.TextBody is not null)
            _logger.LogDebug(
                "[GraphApi] {MessageId}: plain-text alternative not carried over — the Graph message body " +
                "holds one rendering and the HTML one takes precedence (Exchange regenerates the text part)",
                messageId);

        // Headers Graph has no place for: it accepts custom internet headers only with an
        // "x-" prefix, so everything else (Date, Received, List-*, Auto-Submitted, …) is
        // dropped. Per-message flow detail → Debug.
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var dropped = mime.Headers
                .Select(h => h.Field)
                .Where(f => !f.StartsWith("x-", StringComparison.OrdinalIgnoreCase)
                         && !RelayedHeaders.Contains(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (dropped.Count > 0)
                _logger.LogDebug(
                    "[GraphApi] {MessageId}: {Count} header(s) not carried over (Graph accepts custom headers only with an x- prefix): {Headers}",
                    messageId, dropped.Count, string.Join(", ", dropped));
        }
    }

    /// <summary>Headers that reach the recipient through a mapped Graph property rather than verbatim.</summary>
    private static readonly HashSet<string> RelayedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Subject", "From", "To", "Cc", "Bcc", "Reply-To", "Sender", "Message-Id",
        "In-Reply-To", "References", "Importance", "Priority", "Sensitivity",
        "Disposition-Notification-To", "Return-Receipt-To",
        "Content-Type", "Content-Transfer-Encoding", "Mime-Version",
    };

    /// <summary>
    /// Splits message attachments into small (&lt; 3 MB) and large (≥ 3 MB) lists.
    /// Classification comes from <see cref="MimeMessageSplitter"/> (RFC-2183-lenient:
    /// no part is silently discarded). Inline parts (embedded images etc.) keep their
    /// ContentId and IsInline flag so <c>&lt;img src="cid:…"&gt;</c> references in the
    /// HTML body keep working — without them the image shows up as a visible attachment
    /// instead. Attached messages (message/rfc822) are forwarded byte-exact as .eml files.
    /// </summary>
    internal static (List<FileAttachment> Small, List<LargeAttachment> Large)
        CollectAttachments(MimeMessage message)
        => CollectAttachments(MimeMessageSplitter.Split(message));

    internal static (List<FileAttachment> Small, List<LargeAttachment> Large)
        CollectAttachments(MimeMessageSplitter.SplitResult split)
    {
        var small = new List<FileAttachment>();
        var large = new List<LargeAttachment>();
        var unnamed = 0;

        foreach (var (entity, isInline) in split.Attachments)
        {
            string name;
            string mediaType;
            string? contentId;
            byte[] bytes;

            switch (entity)
            {
                case MimePart part:
                {
                    name = part.FileName ?? FallbackFileName(part.ContentType.MimeType, ++unnamed);
                    mediaType = part.ContentType.MimeType;
                    contentId = string.IsNullOrWhiteSpace(part.ContentId) ? null : part.ContentId;

                    using var ms = new MemoryStream();
                    part.Content?.DecodeTo(ms);
                    bytes = ms.ToArray();
                    break;
                }
                case MessagePart rfc822:
                {
                    // Attached e-mail → standard .eml file attachment: byte-exact, and
                    // every mail client opens it as the original message.
                    var dispositionName = rfc822.ContentDisposition?.FileName;
                    name = !string.IsNullOrWhiteSpace(dispositionName)
                        ? dispositionName
                        : string.IsNullOrWhiteSpace(rfc822.Message?.Subject)
                            ? "attached-message" : rfc822.Message.Subject;
                    if (!name.EndsWith(".eml", StringComparison.OrdinalIgnoreCase))
                        name += ".eml";
                    mediaType = rfc822.ContentType.MimeType;
                    contentId = null;

                    using var ms = new MemoryStream();
                    rfc822.Message?.WriteTo(ms);
                    bytes = ms.ToArray();
                    break;
                }
                default:
                    continue; // unreachable: the splitter emits only MimePart/MessagePart
            }

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

    /// <summary>
    /// File name for a part that announces none. The extension is derived from the media
    /// type, so an unnamed <c>text/calendar</c> alternative arrives as <c>invite-1.ics</c>
    /// and is recognised as a meeting request instead of a nameless "attachment" blob.
    /// The counter keeps several unnamed parts of one message apart.
    /// </summary>
    internal static string FallbackFileName(string mimeType, int index)
    {
        var stem = mimeType.Equals("text/calendar", StringComparison.OrdinalIgnoreCase)
            ? "invite"
            : "attachment";

        var extension = MimeTypes.TryGetExtension(mimeType, out var ext) && !string.IsNullOrEmpty(ext)
            ? "." + ext.TrimStart('.')
            : string.Empty;

        return $"{stem}-{index}{extension}";
    }

    /// <summary>
    /// Converts a header address list to Graph recipients. When <paramref name="envelope"/>
    /// is given, addresses missing from the SMTP envelope are left out — they were never
    /// RCPT TO'd and must not be delivered to.
    /// </summary>
    private static List<Recipient> ConvertAddressList(InternetAddressList list, HashSet<string>? envelope = null)
    {
        return list.Mailboxes
            .Where(m => envelope is null || envelope.Contains(m.Address))
            .Select(m => new Recipient { EmailAddress = new EmailAddress { Address = m.Address, Name = m.Name } })
            .ToList();
    }

    /// <summary>
    /// Header addresses that are not in the SMTP envelope. Delivering to them would invent
    /// recipients the sending client never asked for; the caller logs them.
    /// </summary>
    internal static List<string> FindNonEnvelopeRecipients(
        MimeMessage mime, IReadOnlyList<string> envelopeRecipients)
    {
        var envelope = new HashSet<string>(envelopeRecipients, StringComparer.OrdinalIgnoreCase);
        return mime.To.Mailboxes.Concat(mime.Cc.Mailboxes)
            .Select(m => m.Address)
            .Where(addr => !envelope.Contains(addr))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Recipient? ConvertMailbox(MailboxAddress? mailbox) =>
        mailbox is null
            ? null
            : new Recipient { EmailAddress = new EmailAddress { Address = mailbox.Address, Name = mailbox.Name } };

    // -------------------------------------------------------------------------
    // Notification helper (used by AdminNotificationService)
    // -------------------------------------------------------------------------

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
        string bodyHtml,
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
            Body = new ItemBody { ContentType = BodyType.Html, Content = bodyHtml },
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
