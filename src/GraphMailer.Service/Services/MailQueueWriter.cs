using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphMailer.Service.Services;

/// <summary>Represents the metadata written alongside each queued .eml file.</summary>
public sealed class MailMetadata
{
    public string MessageId { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public List<string> To { get; set; } = [];
    public string Subject { get; set; } = string.Empty;
    public string SmtpMessageId { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
    public string ClientIp { get; set; } = string.Empty;
    public int RetryCount { get; set; }
    public string Status { get; set; } = "queued";
    public DateTime? NextRetryAt { get; set; }
    /// <summary>UTC time of the most recent failed delivery attempt.</summary>
    public DateTime? LastAttemptAt { get; set; }
    /// <summary>Error message of the most recent failed delivery attempt.</summary>
    public string? LastError { get; set; }
    /// <summary>UTC time of the successful Graph delivery.</summary>
    public DateTime? SentAt { get; set; }
    /// <summary>
    /// True for system-generated messages (NDRs, admin notifications) queued by the
    /// service itself. When such a message permanently fails, no NDR is generated for
    /// it — prevents NDR-for-NDR loops. Absent in older meta files → false.
    /// </summary>
    public bool IsNotification { get; set; }

    // Reception statistics (schema v2 metrics). Absent in older meta files → 0.

    /// <summary>Recipients listed in the Cc header.</summary>
    public int CcCount { get; set; }
    /// <summary>Envelope recipients that appear in neither the To nor the Cc header.</summary>
    public int BccCount { get; set; }
    public int AttachmentCount { get; set; }
    /// <summary>Approximate total size of all attachments (raw encoded bytes).</summary>
    public long AttachmentBytes { get; set; }
}

[JsonSerializable(typeof(MailMetadata))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class MailMetadataJsonContext : JsonSerializerContext { }

/// <summary>
/// Writes incoming SMTP messages to the on-disk mail queue.
///
/// For each message two files are created atomically (write then rename) in
/// <c>mail/queue/</c> relative to the working directory:
///   {messageId}.eml      – raw RFC-5321 message bytes as received
///   {messageId}.meta.json – metadata (sender, recipients, timestamp, IP)
/// </summary>
internal sealed class MailQueueWriter
{
    private readonly ILogger<MailQueueWriter> _logger;
    private readonly string _queuePath;

    public MailQueueWriter(
        IOptionsMonitor<MailQueueOptions> options,
        ILogger<MailQueueWriter> logger)
    {
        _logger = logger;
        var mailDir = string.IsNullOrEmpty(options.CurrentValue.MailDir)
            ? AppPaths.MailDir
            : options.CurrentValue.MailDir;
        _queuePath = Path.Combine(mailDir, "queue");
        Directory.CreateDirectory(_queuePath);
    }

    /// <summary>
    /// Writes the raw EML bytes and a matching meta.json file to the queue directory.
    /// Files are written to a temporary name first, then renamed to make the
    /// appearance of both files as atomic as possible.
    /// </summary>
    public async Task<MailMetadata> WriteAsync(
        string from,
        IReadOnlyList<string> recipients,
        string clientIp,
        ReadOnlyMemory<byte> emlData,
        CancellationToken ct = default,
        bool isNotification = false)
    {
        var messageId = Guid.NewGuid().ToString("N");
        var info = ExtractMessageInfo(emlData, recipients);

        var tmpEml = Path.Combine(_queuePath, $"{messageId}.eml.tmp");
        var tmpMeta = Path.Combine(_queuePath, $"{messageId}.meta.json.tmp");
        var finalEml = Path.Combine(_queuePath, $"{messageId}.eml");
        var finalMeta = Path.Combine(_queuePath, $"{messageId}.meta.json");

        try
        {
            await File.WriteAllBytesAsync(tmpEml, emlData.ToArray(), ct);

            var meta = new MailMetadata
            {
                MessageId = messageId,
                From = from,
                To = [.. recipients],
                Subject = info.Subject,
                SmtpMessageId = info.SmtpMessageId,
                ReceivedAt = DateTime.UtcNow,
                ClientIp = clientIp,
                Status = "queued",
                IsNotification = isNotification,
                CcCount = info.CcCount,
                BccCount = info.BccCount,
                AttachmentCount = info.AttachmentCount,
                AttachmentBytes = info.AttachmentBytes,
            };

            var json = JsonSerializer.Serialize(meta, MailMetadataJsonContext.Default.MailMetadata);
            await File.WriteAllTextAsync(tmpMeta, json, ct);

            File.Move(tmpEml, finalEml, overwrite: false);
            File.Move(tmpMeta, finalMeta, overwrite: false);

            _logger.LogInformation(
                "[MailQueue] Queued {MessageId} | From: {From} | To: {Recipients} | Subject: {Subject} (IP: {Ip})",
                messageId, from, string.Join(", ", recipients), info.Subject, clientIp);

            return meta;
        }
        catch
        {
            // Clean up temp files on failure
            if (File.Exists(tmpEml)) File.Delete(tmpEml);
            if (File.Exists(tmpMeta)) File.Delete(tmpMeta);
            throw;
        }
    }

    /// <summary>Metadata extracted from the raw EML for logs, metrics and the ConfigTool.</summary>
    internal readonly record struct MessageInfo(
        string Subject, string SmtpMessageId, int CcCount, int BccCount,
        int AttachmentCount, long AttachmentBytes);

    /// <summary>
    /// Parses the EML once with MimeKit and extracts Subject, Message-ID, recipient header
    /// counts and attachment statistics. BCC is derived, not read from a header: envelope
    /// recipients that appear in neither the To nor the Cc header were blind-copied.
    /// Metadata only — a parse failure degrades to header-less defaults and never fails
    /// the queue write.
    /// </summary>
    internal static MessageInfo ExtractMessageInfo(ReadOnlyMemory<byte> emlData, IReadOnlyList<string> envelopeRecipients)
    {
        try
        {
            // Avoid duplicating a potentially large message: wrap the backing array when possible.
            using var stream = System.Runtime.InteropServices.MemoryMarshal.TryGetArray(emlData, out var segment)
                ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
                : new MemoryStream(emlData.ToArray(), writable: false);

            var mime = MimeKit.MimeMessage.Load(stream);

            var headerAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mailbox in mime.To.Mailboxes) headerAddresses.Add(mailbox.Address);
            var ccCount = 0;
            foreach (var mailbox in mime.Cc.Mailboxes) { ccCount++; headerAddresses.Add(mailbox.Address); }
            var bccCount = envelopeRecipients.Count(r => !headerAddresses.Contains(r));

            var attachmentCount = 0;
            long attachmentBytes = 0;
            foreach (var attachment in mime.Attachments)
            {
                attachmentCount++;
                // Raw (still-encoded) content size — close enough for statistics and
                // avoids decoding every attachment just to measure it.
                if (attachment is MimeKit.MimePart { Content.Stream.CanSeek: true } part)
                    attachmentBytes += part.Content.Stream.Length;
            }

            return new MessageInfo(
                Subject: mime.Subject ?? string.Empty,
                SmtpMessageId: (mime.MessageId ?? string.Empty).Trim('<', '>'),
                CcCount: ccCount,
                BccCount: bccCount,
                AttachmentCount: attachmentCount,
                AttachmentBytes: attachmentBytes);
        }
        catch
        {
            return new MessageInfo(string.Empty, string.Empty, 0, 0, 0, 0);
        }
    }
}
