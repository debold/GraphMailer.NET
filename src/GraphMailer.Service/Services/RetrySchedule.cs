using System.Globalization;
using System.Text;
using GraphMailer.Service.Configuration;

namespace GraphMailer.Service.Services;

/// <summary>
/// Single source of truth for the queue's two-phase, time-based retry policy (modelled on
/// Microsoft Exchange, aligned with RFC 5321 §4.5.4.1). Used by <see cref="QueueProcessor"/> to
/// schedule the next attempt and decide give-up, and by the ConfigTool to show the operator the
/// concrete derived schedule.
///
/// Phase 1 (transient): the first <see cref="MailQueueOptions.TransientRetryCount"/> retries are
/// spaced <see cref="MailQueueOptions.TransientRetryIntervalSeconds"/> apart for short blips.
/// Phase 2 (steady): every <see cref="MailQueueOptions.RetryIntervalSeconds"/> thereafter.
/// Give-up: once <see cref="MailQueueOptions.MessageExpirationHours"/> have elapsed since the
/// message was received, it is moved to the failed queue (NDR), regardless of attempt count.
/// </summary>
internal static class RetrySchedule
{
    /// <summary>Seconds to wait before the retry that follows the failure with 1-based <paramref name="retryCount"/>.</summary>
    internal static int NextRetryIntervalSeconds(int retryCount, MailQueueOptions opts)
        => retryCount <= opts.TransientRetryCount
            ? opts.TransientRetryIntervalSeconds
            : opts.RetryIntervalSeconds;

    /// <summary>True when a message received at <paramref name="receivedAtUtc"/> has exceeded its expiration budget by <paramref name="nowUtc"/>.</summary>
    internal static bool HasExpired(DateTime receivedAtUtc, DateTime nowUtc, int expirationHours)
        => nowUtc - receivedAtUtc >= TimeSpan.FromHours(expirationHours);

    /// <summary>Approximate number of delivery attempts within the expiration window (for display).</summary>
    internal static int ApproxAttempts(MailQueueOptions o)
    {
        var total = o.MessageExpirationHours * 3600L;
        var transientSpan = (long)o.TransientRetryCount * o.TransientRetryIntervalSeconds;
        // 1 initial attempt + transient retries + steady retries filling the remaining window.
        var attempts = 1 + o.TransientRetryCount;
        if (total > transientSpan && o.RetryIntervalSeconds > 0)
            attempts += (int)((total - transientSpan) / o.RetryIntervalSeconds);
        return attempts;
    }

    /// <summary>
    /// Human-readable summary of the derived schedule, e.g.
    /// <c>"First retry after 5m, every 5m for the first 6 retries, then every 15m, until the
    /// message expires 24h after receipt (≈ 100 attempts)."</c>.
    /// </summary>
    internal static string Describe(MailQueueOptions o)
    {
        if (o.MessageExpirationHours <= 0)
            return "Messages are given up immediately on the first delivery failure (expiration 0 h).";

        var t = FormatInterval(o.TransientRetryIntervalSeconds);
        var s = FormatInterval(o.RetryIntervalSeconds);

        if (o.TransientRetryCount <= 0)
            return $"Retry every {s}, until the message expires {o.MessageExpirationHours}h after receipt (≈ {ApproxAttempts(o)} attempts).";

        return $"Retry every {t} for the first {o.TransientRetryCount} retries, then every {s}, " +
               $"until the message expires {o.MessageExpirationHours}h after receipt " +
               $"(first retry after {t}; ≈ {ApproxAttempts(o)} attempts total).";
    }

    /// <summary>Compact interval label: "45s", "5m", "1.5m", "1h".</summary>
    private static string FormatInterval(int seconds)
    {
        if (seconds < 60) return $"{seconds}s";
        if (seconds % 3600 == 0) return $"{seconds / 3600}h";
        return (seconds / 60.0).ToString("0.#", CultureInfo.InvariantCulture) + "m";
    }
}
