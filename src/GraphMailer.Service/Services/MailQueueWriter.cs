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
        var (subject, smtpMessageId) = ExtractHeaders(emlData);

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
                Subject = subject,
                SmtpMessageId = smtpMessageId,
                ReceivedAt = DateTime.UtcNow,
                ClientIp = clientIp,
                Status = "queued",
                IsNotification = isNotification
            };

            var json = JsonSerializer.Serialize(meta, MailMetadataJsonContext.Default.MailMetadata);
            await File.WriteAllTextAsync(tmpMeta, json, ct);

            File.Move(tmpEml, finalEml, overwrite: false);
            File.Move(tmpMeta, finalMeta, overwrite: false);

            _logger.LogInformation(
                "[MailQueue] Queued {MessageId} | From: {From} | To: {Recipients} | Subject: {Subject} (IP: {Ip})",
                messageId, from, string.Join(", ", recipients), subject, clientIp);

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

    /// <summary>
    /// Extracts Subject and Message-ID from the header section of an EML message using
    /// MimeKit's header parser: no size cutoff (large Received/DKIM blocks no longer lose
    /// the subject), proper unfolding, and RFC 2047 encoded-word decoding (UTF-8 subjects
    /// become readable in logs, metrics and the ConfigTool). Metadata only — a parse
    /// failure never fails the queue write.
    /// </summary>
    internal static (string Subject, string SmtpMessageId) ExtractHeaders(ReadOnlyMemory<byte> emlData)
    {
        try
        {
            // Avoid duplicating a potentially large message: wrap the backing array when
            // possible (HeaderList.Load stops reading at the blank line anyway).
            using var stream = System.Runtime.InteropServices.MemoryMarshal.TryGetArray(emlData, out var segment)
                ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
                : new MemoryStream(emlData.ToArray(), writable: false);

            var headers = MimeKit.HeaderList.Load(stream);
            var subject = headers[MimeKit.HeaderId.Subject] ?? string.Empty;
            var smtpMessageId = (headers[MimeKit.HeaderId.MessageId] ?? string.Empty).Trim('<', '>');
            return (subject, smtpMessageId);
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }
}
