using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Metrics;
using GraphMailer.Service.Infrastructure.Rules;
using GraphMailer.Service.Infrastructure.Security;
using GraphMailer.Service.Infrastructure.Security.Amsi;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpServer;
using SmtpServer.Protocol;
using SmtpServer.Storage;
using System.Buffers;
using System.Diagnostics;

namespace GraphMailer.Service.Infrastructure.Smtp;

/// <summary>
/// SmtpServer IMessageStore implementation.
/// Receives the complete RFC-5321 message and hands it off to MailQueueWriter.
/// Also applies IP blocking, the malware scan and the message rules as the final gates before
/// queueing — in that order, so the scanner always sees the message as the client sent it.
/// </summary>
internal sealed class SmtpMessageStore : MessageStore
{
    private readonly MailQueueWriter _queue;
    private readonly IpBlockingService _ipBlocking;
    private readonly IMetricsService _metrics;
    private readonly IMailContentScanner _scanner;
    private readonly IOptionsMonitor<MalwareScanOptions> _scanOptions;
    private readonly BlockedMessageRecorder _blockedRecorder;
    private readonly IAdminNotificationService _notifications;
    private readonly MessageRuleProcessor _rules;
    private readonly ILogger<SmtpMessageStore> _logger;

    public SmtpMessageStore(
        MailQueueWriter queue,
        IpBlockingService ipBlocking,
        IMetricsService metrics,
        IMailContentScanner scanner,
        IOptionsMonitor<MalwareScanOptions> scanOptions,
        BlockedMessageRecorder blockedRecorder,
        IAdminNotificationService notifications,
        MessageRuleProcessor rules,
        ILogger<SmtpMessageStore> logger)
    {
        _queue = queue;
        _ipBlocking = ipBlocking;
        _metrics = metrics;
        _scanner = scanner;
        _scanOptions = scanOptions;
        _blockedRecorder = blockedRecorder;
        _notifications = notifications;
        _rules = rules;
        _logger = logger;
    }

    public override async Task<SmtpResponse> SaveAsync(
        ISessionContext context,
        IMessageTransaction transaction,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken)
    {
        var receivedAt = DateTime.UtcNow;
        var remoteIp = IpFilterService.GetRemoteIp(context) ?? "unknown";
        var listenerPort = GetListenerPort(context);
        var sizeBytes = buffer.Length;

        _logger.LogDebug("[SmtpRelay] DATA received from {Ip}: {Size} bytes", remoteIp, sizeBytes);

        if (_ipBlocking.IsBlocked(remoteIp, out var blockedUntil))
        {
            _logger.LogWarning(
                "[SmtpRelay] Message rejected – IP {Ip} is blocked after repeated failures (until {Expires:HH:mm:ss} UTC)",
                remoteIp, blockedUntil);
            await RecordRejectionSafeAsync(RejectionReasons.IpBlocked, remoteIp, listenerPort);
            return SmtpResponse.MailboxUnavailable;
        }

        var from = transaction.From is not null
            ? $"{transaction.From.User}@{transaction.From.Host}"
            : string.Empty;
        // Not var: the message rules below may hand back a different list.
        IReadOnlyList<string> recipients = transaction.To.Select(m => $"{m.User}@{m.Host}").ToArray();

        // Materialised once: the scan and the queue write both need the bytes, and copying a
        // 25 MB message twice per delivery is pure waste.
        var emlBytes = buffer.ToArray();
        var messageId = Guid.NewGuid().ToString("N");

        // The message's history for the ConfigTool timeline. Collected here and written once the
        // SMTP outcome is decided, in one transaction — so a message never shows up half-told.
        var trace = new List<MessageTraceEvent>
        {
            new(receivedAt, TraceStages.Smtp, TraceOutcomes.Ok,
                $"Received from {remoteIp} on port {listenerPort}",
                DescribeSession(context, sizeBytes, recipients.Count)),
        };

        // Last and most expensive gate. Sender/recipient policy already ran in SmtpMailboxFilter,
        // so nothing cheap is left to reject on by the time we start decoding attachments.
        var scanRejection = await ScanForMalwareAsync(
            context, emlBytes, messageId, from, recipients, remoteIp, listenerPort, trace, cancellationToken);
        if (scanRejection is not null)
        {
            await RecordTraceSafeAsync(messageId, receivedAt, from, recipients, remoteIp, emlBytes,
                subject: null, TraceStatuses.Rejected, trace);
            return scanRejection;
        }

        // Message rules run last, after the scan, and never before it. The scanner has to see
        // the message exactly as the client sent it: a rule that strips attachments would
        // otherwise delete the very part the scan is meant to detect and record, which would
        // make the rule engine an antivirus bypass. A malware verdict also has to outrank a
        // policy verdict — that path already writes evidence and notifies.
        var gate = await ApplyMessageRulesAsync(
            context, emlBytes, messageId, from, recipients, remoteIp, listenerPort, trace, cancellationToken);

        if (gate.Gate == RuleGate.Reject)
        {
            await RecordTraceSafeAsync(messageId, receivedAt, gate.From, gate.Recipients, remoteIp, gate.Eml,
                subject: null, TraceStatuses.Rejected, trace);
            return gate.Response;
        }

        if (gate.Gate == RuleGate.Discard)
        {
            await RecordTraceSafeAsync(messageId, receivedAt, gate.From, gate.Recipients, remoteIp, gate.Eml,
                subject: null, TraceStatuses.Discarded, trace);
            return SmtpResponse.Ok;
        }

        emlBytes = gate.Eml;
        from = gate.From;
        recipients = gate.Recipients;
        sizeBytes = emlBytes.LongLength;

        MailMetadata meta;
        var sw = Stopwatch.StartNew();
        try
        {
            meta = await _queue.WriteAsync(
                from, recipients, remoteIp, emlBytes, messageId: messageId, ct: cancellationToken);
            sw.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SmtpRelay] Failed to queue message from {From}: {Error}", from, ex.Message);
            await RecordRejectionSafeAsync(RejectionReasons.QueueError, remoteIp, listenerPort);
            trace.Add(new(DateTime.UtcNow, TraceStages.Smtp, TraceOutcomes.Failed,
                "Not accepted: the message could not be written to the queue",
                $"451 sent, so the client keeps the message and tries again. {ex.Message}"));
            await RecordTraceSafeAsync(messageId, receivedAt, from, recipients, remoteIp, emlBytes,
                subject: null, TraceStatuses.Rejected, trace);
            // A failed local queue write (disk full, IO error, ACL problem) is transient
            // from the client's point of view: answer 451 so the client keeps the message
            // and retries later. A permanent 554 would make conforming clients discard
            // the mail — silent mail loss on a temporary local condition.
            return new SmtpResponse(SmtpReplyCode.Aborted, "Requested action aborted: local error in processing");
        }

        // The message is durably queued — the SMTP response is decided. Metrics are
        // telemetry only and must not turn an accepted message into an error reply
        // (the client would re-send an already-queued message → duplicate delivery).
        try
        {
            await _metrics.RecordEmailReceivedAsync(new ReceivedEmailEvent
            {
                From = meta.From,
                To = meta.To,
                MessageId = meta.MessageId,
                Subject = meta.Subject,
                SizeBytes = (long)sizeBytes,
                DurationMs = (int)sw.ElapsedMilliseconds,
                ClientIp = remoteIp,
                ListenerPort = listenerPort,
                Tls = context.Pipe?.IsSecure ?? false,
                Authenticated = context.Authentication?.IsAuthenticated ?? false,
                AuthUser = context.Authentication?.User ?? string.Empty,
                CcCount = meta.CcCount,
                BccCount = meta.BccCount,
                AttachmentCount = meta.AttachmentCount,
                AttachmentBytes = meta.AttachmentBytes,
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[SmtpRelay] Failed to record received-mail metrics for {MessageId}: {Error}",
                meta.MessageId, ex.Message);
        }

        trace.Add(new(DateTime.UtcNow, TraceStages.Smtp, TraceOutcomes.Ok,
            "Accepted and queued (250 OK)",
            meta.To.Count == 1 ? $"1 recipient: {meta.To[0]}" : $"{meta.To.Count} recipients: {string.Join(", ", meta.To)}"));
        await RecordTraceSafeAsync(meta.MessageId, receivedAt, meta.From, meta.To, remoteIp, emlBytes,
            meta.Subject, TraceStatuses.Queued, trace);

        return SmtpResponse.Ok;
    }

    /// <summary>Second timeline line of the receipt: how the client connected and what it sent.</summary>
    private static string DescribeSession(ISessionContext context, long sizeBytes, int recipientCount)
    {
        var tls = context.Pipe?.IsSecure ?? false;
        var user = context.Authentication?.IsAuthenticated == true ? context.Authentication.User : null;
        return string.Join(" · ",
            tls ? "TLS" : "no TLS",
            user is null ? "not signed in" : $"signed in as {user}",
            FormatSize(sizeBytes),
            recipientCount == 1 ? "1 recipient" : $"{recipientCount} recipients");
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} bytes",
    };

    /// <summary>
    /// Writes the message's receipt history. Like every statistic it must never change the SMTP
    /// outcome, so all errors are swallowed. The subject is parsed from the message only on the
    /// rare paths that end before the queue writer has done it.
    /// </summary>
    private async Task RecordTraceSafeAsync(
        string messageId, DateTime receivedAt, string from, IReadOnlyList<string> recipients,
        string remoteIp, byte[] eml, string? subject, string status, List<MessageTraceEvent> trace)
    {
        try
        {
            subject ??= MailQueueWriter.ExtractMessageInfo(eml, recipients).Subject;
            await _metrics.RecordTraceAsync(new MessageTraceHeader
            {
                MessageId = messageId,
                ReceivedAtUtc = receivedAt,
                From = from,
                To = recipients,
                Subject = subject,
                ClientIp = remoteIp,
                SizeBytes = eml.LongLength,
            }, status, attempts: 0, trace);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SmtpRelay] Failed to record the message trace for {MessageId}", messageId);
        }
    }

    /// <summary>What the rule engine decided about a message.</summary>
    private enum RuleGate { Proceed, Reject, Discard }

    /// <summary>
    /// The gate's decision plus the message as the rules left it. A struct rather than
    /// <c>out</c> parameters, which an async method cannot have.
    /// </summary>
    private readonly record struct RuleGateResult(
        RuleGate Gate, SmtpResponse Response, byte[] Eml, string From, IReadOnlyList<string> Recipients);

    /// <summary>
    /// Runs the configured message rules and decides what happens to the message.
    ///
    /// Mirrors <see cref="ScanForMalwareAsync"/>'s contract, widened because rules also return
    /// data: the caller queues <see cref="RuleGateResult.Eml"/> with
    /// <see cref="RuleGateResult.Recipients"/>, not the bytes that arrived.
    ///
    /// The engine itself never throws — a rule problem produces an unmodified message, not an
    /// SMTP failure.
    /// </summary>
    private async Task<RuleGateResult> ApplyMessageRulesAsync(
        ISessionContext context,
        byte[] emlBytes,
        string messageId,
        string from,
        IReadOnlyList<string> recipients,
        string remoteIp,
        int listenerPort,
        List<MessageTraceEvent> trace,
        CancellationToken ct)
    {
        if (!_rules.IsActive)
            return new RuleGateResult(RuleGate.Proceed, SmtpResponse.Ok, emlBytes, from, recipients);

        var authUser = context.Authentication?.User ?? string.Empty;
        var session = new RuleSessionFacts
        {
            ClientIp = remoteIp,
            ListenerPort = listenerPort,
            AuthUser = authUser,
            Authenticated = context.Authentication?.IsAuthenticated ?? false,
            Tls = context.Pipe?.IsSecure ?? false,
        };

        var result = _rules.Apply(emlBytes, from, recipients, session, messageId, ct);
        var outcome = result.Outcome;

        // One row per (rule, message) — a rule with five actions counts once.
        foreach (var rule in outcome.Matched)
        {
            await RecordRuleHitSafeAsync(rule.Name, rule.Mode.ToString(), rule.Outcome, listenerPort);
            trace.Add(DescribeRuleHit(rule));
        }

        if (outcome.Verdict == RuleVerdict.Reject)
            trace.Add(new(DateTime.UtcNow, TraceStages.Rules, TraceOutcomes.Failed,
                $"Rejected by rule '{outcome.DecidingRule ?? "(unknown)"}'",
                $"{outcome.SmtpCode} {outcome.SmtpText}"));
        else if (outcome.Verdict == RuleVerdict.Discard)
            trace.Add(new(DateTime.UtcNow, TraceStages.Rules, TraceOutcomes.Failed,
                $"Discarded by rule '{outcome.DecidingRule ?? "(unknown)"}'",
                "The client was told 250 OK, but the message was not queued"));

        switch (outcome.Verdict)
        {
            case RuleVerdict.Reject:
                _logger.LogWarning(
                    "[MessageRules] Message from {From} to {Recipients} rejected by rule {Rule} (IP: {Ip}) – {Code} {Text}",
                    from, string.Join(", ", recipients), outcome.DecidingRule ?? "(unknown)",
                    remoteIp, outcome.SmtpCode, outcome.SmtpText);

                await RecordRejectionSafeAsync(RejectionReasons.RuleRejected, remoteIp, listenerPort);

                // The code and the text are the operator's, already sanitised by the processor —
                // a CR or LF here would end the reply line and let the rest be read as further
                // protocol responses.
                return new RuleGateResult(
                    RuleGate.Reject,
                    new SmtpResponse((SmtpReplyCode)outcome.SmtpCode, outcome.SmtpText),
                    result.Eml, result.EnvelopeFrom, result.Recipients);

            case RuleVerdict.Discard:
                await RecordDiscardAsync(outcome, result, messageId, from, recipients, remoteIp, listenerPort, authUser, ct);
                return new RuleGateResult(
                    RuleGate.Discard, SmtpResponse.Ok, result.Eml, result.EnvelopeFrom, result.Recipients);

            default:
                return new RuleGateResult(
                    RuleGate.Proceed, SmtpResponse.Ok, result.Eml, result.EnvelopeFrom, result.Recipients);
        }
    }

    /// <summary>
    /// Records a silently discarded message. The client is told 250, so nothing else would ever
    /// show that this mail existed — the record under <c>mail\blocked\</c> is the only trace, and
    /// the full message is stored with it when <c>MessageRules.StoreDiscardedMessages</c> is on.
    /// </summary>
    private async Task RecordDiscardAsync(
        MessageRuleOutcome outcome,
        MessageRuleResult result,
        string messageId,
        string from,
        IReadOnlyList<string> recipients,
        string remoteIp,
        int listenerPort,
        string authUser,
        CancellationToken ct)
    {
        var rule = outcome.DecidingRule ?? "(unknown)";
        var info = MailQueueWriter.ExtractMessageInfo(result.Eml, recipients);

        _logger.LogWarning(
            "[MessageRules] Message from {From} to {Recipients} discarded by rule {Rule} (IP: {Ip}) – " +
            "the client was told 250 and the message was not queued",
            from, string.Join(", ", recipients), rule, remoteIp);

        await _blockedRecorder.RecordAsync(new BlockedMessageRecord
        {
            Source = BlockedMessageSources.MessageRule,
            RuleName = rule,
            MessageId = messageId,
            DetectedAt = DateTime.UtcNow,
            From = from,
            To = [.. recipients],
            Subject = info.Subject,
            ClientIp = remoteIp,
            AuthUser = authUser,
            ListenerPort = listenerPort,
            Mode = MessageRuleMode.Enforce.ToString(),
            Blocked = true,
            DetectedIn = "message",
        }, result.Eml, ct);
    }

    /// <summary>Timeline line for one matched rule.</summary>
    internal static MessageTraceEvent DescribeRuleHit(MatchedRule rule)
    {
        var audit = rule.Mode == MessageRuleMode.Audit ? " (audit mode, nothing changed)" : string.Empty;
        var what = rule.Outcome switch
        {
            RuleOutcomes.Modified => "changed the message",
            RuleOutcomes.Rejected => "rejects the message",
            RuleOutcomes.Discarded => "discards the message",
            RuleOutcomes.Skipped => "matched, but its actions were skipped",
            _ => rule.Outcome,
        };
        return new MessageTraceEvent(DateTime.UtcNow, TraceStages.Rules,
            rule.Outcome == RuleOutcomes.Skipped || rule.Mode == MessageRuleMode.Audit ? TraceOutcomes.Info : TraceOutcomes.Ok,
            $"Rule '{rule.Name}' {what}{audit}");
    }

    /// <summary>Statistics must never change the SMTP outcome — swallow all errors.</summary>
    private async Task RecordRuleHitSafeAsync(string ruleName, string mode, string outcome, int listenerPort)
    {
        try
        {
            await _metrics.RecordRuleHitAsync(ruleName, mode, outcome, listenerPort);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MessageRules] Failed to record the rule-hit metric for {Rule}", ruleName);
        }
    }

    /// <summary>
    /// Runs the malware scan and decides the message's fate.
    /// Returns <see langword="null"/> when the message may proceed to the queue, or the SMTP
    /// response to reject it with.
    ///
    /// Every outcome other than a confirmed detection lets the message through — an unavailable
    /// scanner, a timeout, an oversized part. A scanner problem must never become a mail outage,
    /// so the only thing that stops mail here is an actual verdict, and only in Enforce mode.
    ///
    /// Because those outcomes are silent by construction, every one of them is logged: in Audit
    /// mode at Information, so the log is the audit trail the mode is named after, and at Debug in
    /// Enforce, where the rejection is the event worth seeing.
    /// </summary>
    private async Task<SmtpResponse?> ScanForMalwareAsync(
        ISessionContext context,
        byte[] emlBytes,
        string messageId,
        string from,
        IReadOnlyList<string> recipients,
        string remoteIp,
        int listenerPort,
        List<MessageTraceEvent> trace,
        CancellationToken ct)
    {
        var opts = _scanOptions.CurrentValue;
        if (opts.Mode == MalwareScanMode.Off) return null;
        if (!_scanner.IsAvailable)
        {
            trace.Add(new(DateTime.UtcNow, TraceStages.Scan, TraceOutcomes.Warning,
                "Not scanned for malware: no AMSI-capable antivirus is available"));
            return null;
        }

        // Audit mode exists to be watched: an operator runs it to see what the scanner does to
        // real mail before letting it reject any, and "no detections" is only reassuring once the
        // log also shows that messages were in fact being inspected. So every message's
        // disposition — scanned, bypassed, delivered unscanned — is logged at Information here.
        // Enforce keeps the same lines at Debug: there the operator-relevant event is the
        // rejection, and a line per clean message would bury it in routine traffic.
        var auditLevel = opts.Mode == MalwareScanMode.Audit ? LogLevel.Information : LogLevel.Debug;

        var authUser = context.Authentication?.User ?? string.Empty;
        if (TryGetBypassReason(opts, authUser, remoteIp) is { } bypass)
        {
            _logger.Log(auditLevel, "[MalwareScan] {MessageId}: scan skipped – {Reason}", messageId, bypass);
            trace.Add(new(DateTime.UtcNow, TraceStages.Scan, TraceOutcomes.Info, "Malware scan skipped", bypass));
            return null;
        }

        var sw = Stopwatch.StartNew();
        var result = await _scanner.ScanAsync(emlBytes, messageId, ct);
        sw.Stop();

        if (result.Outcome is ScanOutcome.Failed or ScanOutcome.Skipped)
        {
            // Both cases mean the message was delivered without being fully vetted, and both are
            // already logged with their reason by the scanner itself (timeout, error, size limit)
            // — the audit line adds the message's own outcome next to it.
            _logger.Log(auditLevel,
                "[MalwareScan] {MessageId}: scan incomplete ({Outcome}) after {Elapsed}ms – message is delivered unscanned",
                messageId, result.Outcome, sw.ElapsedMilliseconds);
            trace.Add(new(DateTime.UtcNow, TraceStages.Scan, TraceOutcomes.Warning,
                "Malware scan incomplete, delivered unscanned",
                result.Error ?? $"Part '{result.ThreatLocation}' exceeded the scan size limit"));

            // Fire-and-forget: the notification is threshold-based, so a single hiccup stays
            // quiet while a scanner that is consistently failing gets reported.
            _ = _notifications.NotifyMalwareScanFailureAsync(
                result.Error ?? $"part '{result.ThreatLocation}' exceeded the scan size limit", ct);
            return null;
        }

        if (result.Outcome != ScanOutcome.Malware)
        {
            // Clean only — anything else reached one of the branches above, and reporting an
            // unavailable scanner as "no detection" would claim coverage that never happened.
            if (result.Outcome == ScanOutcome.Clean)
            {
                _logger.Log(auditLevel,
                    "[MalwareScan] {MessageId}: scanned {Parts} part(s) of message from {From} ({Ip}) " +
                    "in {Elapsed}ms – no detection",
                    messageId, result.PartsScanned, from, remoteIp, sw.ElapsedMilliseconds);
                trace.Add(new(DateTime.UtcNow, TraceStages.Scan, TraceOutcomes.Ok,
                    "Malware scan: clean",
                    $"{result.PartsScanned} part(s) scanned in {sw.ElapsedMilliseconds} ms"));
            }

            return null;
        }

        if (result.IsAllowlistable && IsAllowlisted(opts, result.Sha256!))
        {
            // Logged at Information, not Debug: a standing exemption that silently swallows
            // detections is exactly the kind of thing that needs to stay visible in the log.
            _logger.LogInformation(
                "[MalwareScan] {MessageId}: detection in '{Part}' from {Ip} allowed by hash allowlist ({Hash})",
                messageId, result.ThreatLocation, remoteIp, result.Sha256);
            trace.Add(new(DateTime.UtcNow, TraceStages.Scan, TraceOutcomes.Info,
                $"Malware finding in '{result.ThreatLocation}' allowed by the hash allowlist",
                result.Sha256));
            return null;
        }

        var enforcing = opts.Mode == MalwareScanMode.Enforce;
        var info = MailQueueWriter.ExtractMessageInfo(emlBytes, recipients);
        var detectedIn = ClassifyLocation(result);

        // Counted in both modes — this is the statistic behind the Status tile, the Metrics page
        // and the periodic report. The rejection metric further down is Enforce-only, because
        // that one answers "what did we refuse", not "what did we find".
        await RecordDetectionMetricSafeAsync(enforcing, detectedIn, remoteIp, listenerPort);

        _logger.LogWarning(
            "[MalwareScan] Malware detected in {Part} of message from {From} to {Recipients} (IP: {Ip}, " +
            "AMSI result {Result}, hash {Hash}) – {Action}",
            result.ThreatLocation, from, string.Join(", ", recipients), remoteIp,
            result.ResultCode, result.Sha256 ?? "n/a",
            enforcing ? "message rejected" : "AUDIT MODE, message delivered");

        await _blockedRecorder.RecordAsync(new BlockedMessageRecord
        {
            MessageId = messageId,
            DetectedAt = DateTime.UtcNow,
            From = from,
            To = [.. recipients],
            Subject = info.Subject,
            ClientIp = remoteIp,
            AuthUser = authUser,
            ListenerPort = listenerPort,
            Mode = opts.Mode.ToString(),
            Blocked = enforcing,
            DetectedIn = detectedIn,
            PartName = result.ThreatLocation ?? string.Empty,
            PartSizeBytes = result.PartSizeBytes,
            PartSha256 = result.Sha256,
            AmsiResult = result.ResultCode,
        }, ct: ct);

        _ = _notifications.NotifyMalwareDetectedAsync(
            from, recipients, remoteIp, info.Subject, result.ThreatLocation ?? string.Empty,
            result.Sha256, blocked: enforcing, ct);

        var findingDetail = $"AMSI result {result.ResultCode}" + (result.Sha256 is null ? string.Empty : $", SHA-256 {result.Sha256}");
        if (!enforcing)
        {
            trace.Add(new(DateTime.UtcNow, TraceStages.Scan, TraceOutcomes.Warning,
                $"Malware found in '{result.ThreatLocation}', delivered anyway (audit mode)", findingDetail));
            return null;
        }

        trace.Add(new(DateTime.UtcNow, TraceStages.Scan, TraceOutcomes.Failed,
            $"Rejected: malware found in '{result.ThreatLocation}'", $"554 sent. {findingDetail}"));
        await RecordRejectionSafeAsync(RejectionReasons.MalwareDetected, remoteIp, listenerPort);

        // Permanent 554: the content is the problem, so a retry of the same message is pointless.
        // The reply stays generic — the file name and hash are operator information, not the
        // sender's, and echoing them back tells a probing client what got through and what did not.
        return new SmtpResponse(SmtpReplyCode.TransactionFailed, "Message rejected: content failed malware scan");
    }

    /// <summary>Statistics must never change the SMTP outcome — swallow all errors.</summary>
    private async Task RecordDetectionMetricSafeAsync(bool blocked, string detectedIn, string clientIp, int listenerPort)
    {
        try
        {
            await _metrics.RecordMalwareDetectionAsync(blocked, detectedIn, clientIp, listenerPort);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MalwareScan] Failed to record the detection metric");
        }
    }

    private static string ClassifyLocation(ScanResult result)
        => result.Sha256 is not null ? "attachment"
         : result.ThreatLocation?.StartsWith("message body", StringComparison.OrdinalIgnoreCase) == true ? "body"
         : "message";

    /// <summary>
    /// Why this message is exempt from scanning, or <see langword="null"/> when it is not.
    ///
    /// Matches on the authenticated user and the client IP only. MAIL FROM is deliberately not
    /// an option: an envelope sender is chosen freely by the client, so an address-based
    /// exemption would let anyone who can reach the listener opt out of scanning.
    /// </summary>
    private static string? TryGetBypassReason(MalwareScanOptions opts, string authUser, string remoteIp)
    {
        if (!string.IsNullOrEmpty(authUser) &&
            opts.BypassAuthenticatedUsers.Any(u => u.Equals(authUser, StringComparison.OrdinalIgnoreCase)))
            return $"authenticated user '{authUser}' is on the scan bypass list";

        if (IpFilterService.IsInAnyRange(remoteIp, opts.BypassIpAddresses))
            return $"client IP {remoteIp} is on the scan bypass list";

        return null;
    }

    private static bool IsAllowlisted(MalwareScanOptions opts, string sha256)
        => opts.AllowedContentHashes.Any(
            h => !string.IsNullOrWhiteSpace(h.Sha256) &&
                 h.Sha256.Trim().Equals(sha256, StringComparison.OrdinalIgnoreCase));

    /// <summary>Local listener port of the session (0 when unavailable).</summary>
    internal static int GetListenerPort(ISessionContext context)
    {
        try
        {
            return (context.EndpointDefinition.Endpoint as System.Net.IPEndPoint)?.Port ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Rejection metrics must never influence the SMTP response — swallow all errors.</summary>
    private async Task RecordRejectionSafeAsync(string reason, string clientIp, int listenerPort)
    {
        try
        {
            await _metrics.RecordRejectionAsync(reason, clientIp, listenerPort);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SmtpRelay] Failed to record rejection metric {Reason}", reason);
        }
    }
}
