using System.Text.Json;
using System.Text.Json.Serialization;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphMailer.Service.Services;

/// <summary>Which subsystem stopped the message.</summary>
public static class BlockedMessageSources
{
    /// <summary>The malware scan. Also the value assumed for records written before this field existed.</summary>
    public const string MalwareScan = "MalwareScan";

    /// <summary>A message rule discarded the message.</summary>
    public const string MessageRule = "MessageRule";
}

/// <summary>
/// Metadata about a message that was stopped before delivery.
///
/// For a malware finding this is deliberately metadata only — the message itself is discarded,
/// so nothing malicious is ever written to disk. That rules out releasing a false positive from
/// here (the sender has to resend), but it also means the record cannot re-infect anything, and
/// the on-access scanner cannot quarantine it out from under us.
///
/// A message a rule discarded is a different case: it is not malicious, and an operator
/// debugging a silent drop needs the content. Storing it is therefore possible, but only when
/// <c>MessageRules.StoreDiscardedMessages</c> is switched on.
/// </summary>
public sealed class BlockedMessageRecord
{
    /// <summary>
    /// One of <see cref="BlockedMessageSources"/>. Absent in records written before message
    /// rules existed, which is why the reader treats an empty value as the malware scan.
    /// </summary>
    public string Source { get; set; } = BlockedMessageSources.MalwareScan;

    /// <summary>The rule that discarded the message; empty for a malware finding.</summary>
    public string RuleName { get; set; } = string.Empty;

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
    private readonly IOptionsMonitor<MessageRulesOptions> _ruleOptions;
    private readonly ILogger<BlockedMessageRecorder> _logger;
    private readonly string _blockedPath;

    public BlockedMessageRecorder(
        IOptionsMonitor<MailQueueOptions> queueOptions,
        IOptionsMonitor<MalwareScanOptions> scanOptions,
        IOptionsMonitor<MessageRulesOptions> ruleOptions,
        ILogger<BlockedMessageRecorder> logger)
    {
        _scanOptions = scanOptions;
        _ruleOptions = ruleOptions;
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
    /// <param name="emlBytes">
    /// The message itself, stored alongside the record only for a rule discard and only when
    /// <c>MessageRules.StoreDiscardedMessages</c> is on. Never passed for a malware finding.
    /// </param>
    public async Task RecordAsync(
        BlockedMessageRecord record, CancellationToken ct = default, byte[]? emlBytes = null)
    {
        var component = record.Source == BlockedMessageSources.MessageRule ? "MessageRules" : "MalwareScan";
        try
        {
            var tmp = Path.Combine(_blockedPath, $"{record.MessageId}.meta.json.tmp");
            var final = Path.Combine(_blockedPath, $"{record.MessageId}.meta.json");

            var json = JsonSerializer.Serialize(record, BlockedMessageRecordJsonContext.Default.BlockedMessageRecord);
            await File.WriteAllTextAsync(tmp, json, ct);
            File.Move(tmp, final, overwrite: true);

            if (emlBytes is not null
                && record.Source == BlockedMessageSources.MessageRule
                && _ruleOptions.CurrentValue.StoreDiscardedMessages)
            {
                var emlTmp = Path.Combine(_blockedPath, $"{record.MessageId}.eml.tmp");
                var emlFinal = Path.Combine(_blockedPath, $"{record.MessageId}.eml");
                await File.WriteAllBytesAsync(emlTmp, emlBytes, ct);
                File.Move(emlTmp, emlFinal, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[{Component}] Failed to write the blocked-message record for {MessageId}: {Error}",
                component, record.MessageId, ex.Message);
        }
    }

    /// <summary>
    /// Deletes expired records. Each record is aged against the retention of the subsystem that
    /// wrote it — <see cref="MalwareScanOptions.BlockedRecordRetentionDays"/> for a detection,
    /// <see cref="MessageRulesOptions.DiscardRecordRetentionDays"/> for a rule discard. The two
    /// are separate on purpose: the malware setting lives on a page that has nothing to do with
    /// rules and applies even when scanning is switched off entirely, so sharing it would let a
    /// setting nobody is looking at delete the other subsystem's evidence.
    ///
    /// 0 keeps a subsystem's records forever. Called from the queue processor's hourly maintenance.
    /// </summary>
    internal void CleanupExpiredRecords(CancellationToken ct = default)
    {
        if (!Directory.Exists(_blockedPath)) return;

        var scanRetention = _scanOptions.CurrentValue.BlockedRecordRetentionDays;
        var ruleRetention = _ruleOptions.CurrentValue.DiscardRecordRetentionDays;
        if (scanRetention <= 0 && ruleRetention <= 0) return;

        var now = DateTime.UtcNow;
        var deletedScan = 0;
        var deletedRules = 0;

        foreach (var path in Directory.GetFiles(_blockedPath, "*.meta.json"))
        {
            if (ct.IsCancellationRequested) break;

            var isRule = ReadSource(path) == BlockedMessageSources.MessageRule;
            var retentionDays = isRule ? ruleRetention : scanRetention;
            if (retentionDays <= 0) continue;

            if (File.GetLastWriteTimeUtc(path) >= now.AddDays(-retentionDays)) continue;

            try
            {
                File.Delete(path);

                // A stored discard has its message next to the record; the pair goes together.
                var eml = Path.ChangeExtension(Path.ChangeExtension(path, null), ".eml");
                if (File.Exists(eml)) File.Delete(eml);

                if (isRule) deletedRules++; else deletedScan++;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[MalwareScan] Could not delete expired record {Path}", path);
            }
        }

        if (deletedScan > 0)
            _logger.LogInformation(
                "[MalwareScan] Retention cleanup: deleted {Count} detection record(s) older than {Days} day(s)",
                deletedScan, scanRetention);

        if (deletedRules > 0)
            _logger.LogInformation(
                "[MessageRules] Retention cleanup: deleted {Count} discard record(s) older than {Days} day(s)",
                deletedRules, ruleRetention);
    }

    /// <summary>
    /// The <c>Source</c> of a record on disk. An unreadable or pre-<c>Source</c> record counts as
    /// a malware detection — that is what every record written before this field existed was.
    /// </summary>
    private static string ReadSource(string path)
    {
        try
        {
            var record = JsonSerializer.Deserialize(
                File.ReadAllText(path), BlockedMessageRecordJsonContext.Default.BlockedMessageRecord);
            return string.IsNullOrWhiteSpace(record?.Source)
                ? BlockedMessageSources.MalwareScan
                : record.Source;
        }
        catch
        {
            return BlockedMessageSources.MalwareScan;
        }
    }
}
