using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Metrics;
using GraphMailer.Service.Infrastructure.Security;
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
/// Also applies IP filter / IP blocking as a final gate before queueing.
/// </summary>
internal sealed class SmtpMessageStore : MessageStore
{
    private readonly MailQueueWriter _queue;
    private readonly IpBlockingService _ipBlocking;
    private readonly IOptionsMonitor<SmtpAccessOptions> _access;
    private readonly IMetricsService _metrics;
    private readonly ILogger<SmtpMessageStore> _logger;

    public SmtpMessageStore(
        MailQueueWriter queue,
        IpBlockingService ipBlocking,
        IOptionsMonitor<SmtpAccessOptions> access,
        IMetricsService metrics,
        ILogger<SmtpMessageStore> logger)
    {
        _queue = queue;
        _ipBlocking = ipBlocking;
        _access = access;
        _metrics = metrics;
        _logger = logger;
    }

    public override async Task<SmtpResponse> SaveAsync(
        ISessionContext context,
        IMessageTransaction transaction,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken)
    {
        var remoteIp = IpFilterService.GetRemoteIp(context) ?? "unknown";
        var sizeBytes = buffer.Length;

        _logger.LogDebug("[SmtpRelay] DATA received from {Ip}: {Size} bytes", remoteIp, sizeBytes);

        if (_ipBlocking.IsBlocked(remoteIp, out var blockedUntil))
        {
            _logger.LogWarning(
                "[SmtpRelay] Message rejected – IP {Ip} is blocked after repeated failures (until {Expires:HH:mm:ss} UTC)",
                remoteIp, blockedUntil);
            return SmtpResponse.MailboxUnavailable;
        }

        var from = transaction.From is not null
            ? $"{transaction.From.User}@{transaction.From.Host}"
            : string.Empty;
        var recipients = transaction.To.Select(m => $"{m.User}@{m.Host}").ToArray();

        MailMetadata meta;
        var sw = Stopwatch.StartNew();
        try
        {
            meta = await _queue.WriteAsync(from, recipients, remoteIp, buffer.ToArray(), cancellationToken);
            sw.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SmtpRelay] Failed to queue message from {From}: {Error}", from, ex.Message);
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
            await _metrics.RecordEmailReceivedAsync(meta.From, meta.To, meta.MessageId, meta.Subject,
                sizeBytes: (long)sizeBytes, durationMs: (int)sw.ElapsedMilliseconds, clientIp: remoteIp, ct: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[SmtpRelay] Failed to record received-mail metrics for {MessageId}: {Error}",
                meta.MessageId, ex.Message);
        }

        return SmtpResponse.Ok;
    }
}
