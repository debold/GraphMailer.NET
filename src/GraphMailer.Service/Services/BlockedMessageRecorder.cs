using System.Text.Json;
using System.Text.Json.Serialization;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphMailer.Service.Services;

/// <summary>
/// Metadata about a message a malware scan flagged. Deliberately metadata only — the message
/// itself is discarded, so nothing malicious is ever written to disk. That rules out releasing
/// a false positive from here (the sender has to resend), but it also means the record cannot
/// re-infect anything, and the on-access scanner cannot quarantine it out from under us.
/// </summary>
public sealed class BlockedMessageRecord
{
    public string MessageId { get; set; } = string.Empty;
    public DateTime DetectedAt { get; set; }
    public string From { get; set; } = string.Empty;
    public List<string> To { get; set; } = [];
    public string Subject { get; set; } = string.Empty;
    public string ClientIp { get; set; } = string.Empty;
    public string AuthUser { get; set; } = string.Empty;
    public int ListenerPort { get; set; }

    /// <summary>Scan mode at the time of detection — "Audit" or "Enforce".</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>False in audit mode: detected and recorded, but the message was delivered.</summary>
    public bool Blocked { get; set; }

    /// <summary>"attachment", "body" or "message" (unparsable MIME scanned whole).</summary>
    public string DetectedIn { get; set; } = string.Empty;

    /// <summary>
    /// Attachment file name or body label, as it appeared in the message. Untrusted input, and
    /// only ever stored as a JSON value — the record's own file name is the generated message id.
    /// </summary>
    public string PartName { get; set; } = string.Empty;

    public long PartSizeBytes { get; set; }

    /// <summary>SHA-256 of the offending attachment; null for body hits, which cannot be allowlisted.</summary>
    public string? PartSha256 { get; set; }

    /// <summary>Raw AMSI_RESULT. There is no threat name — AMSI does not report one.</summary>
    public uint AmsiResult { get; set; }
}

[JsonSerializable(typeof(BlockedMessageRecord))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class BlockedMessageRecordJsonContext : JsonSerializerContext { }

/// <summary>
/// Writes and prunes the records under <c>mail\blocked\</c>. Separate from
/// <see cref="MailQueueWriter"/> because nothing here is a queued message: these files are
/// evidence, never input to delivery.
/// </summary>
internal sealed class BlockedMessageRecorder
{
    private readonly IOptionsMonitor<MalwareScanOptions> _scanOptions;
    private readonly ILogger<BlockedMessageRecorder> _logger;
    private readonly string _blockedPath;

    public BlockedMessageRecorder(
        IOptionsMonitor<MailQueueOptions> queueOptions,
        IOptionsMonitor<MalwareScanOptions> scanOptions,
        ILogger<BlockedMessageRecorder> logger)
    {
        _scanOptions = scanOptions;
        _logger = logger;

        var mailDir = string.IsNullOrEmpty(queueOptions.CurrentValue.MailDir)
            ? AppPaths.MailDir
            : queueOptions.CurrentValue.MailDir;
        _blockedPath = Path.Combine(mailDir, "blocked");
        Directory.CreateDirectory(_blockedPath);
    }

    internal string BlockedPath => _blockedPath;

    /// <summary>
    /// Persists one record. Never throws: the message has already been dealt with by the time
    /// this runs, and a failed evidence write must not change the SMTP reply.
    /// </summary>
    public async Task RecordAsync(BlockedMessageRecord record, CancellationToken ct = default)
    {
        try
        {
            var tmp = Path.Combine(_blockedPath, $"{record.MessageId}.meta.json.tmp");
            var final = Path.Combine(_blockedPath, $"{record.MessageId}.meta.json");

            var json = JsonSerializer.Serialize(record, BlockedMessageRecordJsonContext.Default.BlockedMessageRecord);
            await File.WriteAllTextAsync(tmp, json, ct);
            File.Move(tmp, final, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[MalwareScan] Failed to write the blocked-message record for {MessageId}: {Error}",
                record.MessageId, ex.Message);
        }
    }

    /// <summary>
    /// Deletes records past <see cref="MalwareScanOptions.BlockedRecordRetentionDays"/>.
    /// 0 keeps them forever. Called from the queue processor's hourly maintenance.
    /// </summary>
    internal void CleanupExpiredRecords(CancellationToken ct = default)
    {
        var retentionDays = _scanOptions.CurrentValue.BlockedRecordRetentionDays;
        if (retentionDays <= 0 || !Directory.Exists(_blockedPath)) return;

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var deleted = 0;

        foreach (var path in Directory.GetFiles(_blockedPath, "*.meta.json"))
        {
            if (ct.IsCancellationRequested) break;
            if (File.GetLastWriteTimeUtc(path) >= cutoff) continue;

            try
            {
                File.Delete(path);
                deleted++;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[MalwareScan] Could not delete expired record {Path}", path);
            }
        }

        if (deleted > 0)
            _logger.LogInformation(
                "[MalwareScan] Retention cleanup: deleted {Count} blocked-message record(s) older than {Days} day(s)",
                deleted, retentionDays);
    }
}
