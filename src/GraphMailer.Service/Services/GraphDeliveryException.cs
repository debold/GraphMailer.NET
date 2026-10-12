namespace GraphMailer.Service.Services;

/// <summary>
/// Delivery failure reported by the Microsoft Graph API, classified for the queue
/// processor: <see cref="IsPermanent"/> marks rejections that can never succeed on a
/// retry of the same message (invalid recipient, sender mailbox not found, request
/// too large, hybrid mailbox without EXO REST support). The queue processor fails
/// these immediately — NDR after seconds instead of after the full expiration window.
/// Everything else (throttling, outages, auth/config problems an operator can fix)
/// stays on the normal retry schedule.
/// </summary>
internal sealed class GraphDeliveryException : Exception
{
    public bool IsPermanent { get; }

    /// <summary>
    /// Graph's error code, when the failure came from an API response. Lets the queue processor
    /// refine the classification without parsing <see cref="Exception.Message"/>.
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// Exchange Online refused because the mailbox (or the service in front of it) is
    /// overloaded — throttling, a per-mailbox concurrency limit, a gateway timeout. Says
    /// nothing about the message itself: every other message through the same mailbox
    /// would fail the same way right now, so the queue processor holds them back.
    /// </summary>
    public bool IsThrottled { get; }

    /// <summary>
    /// Of the <see cref="IsThrottled"/> failures, the ones that are actual rate limiting — Graph's
    /// 429 or an Exchange concurrency limit — as opposed to a mailbox or backend that is briefly
    /// unavailable (a plain 503/504 such as <c>MailboxInfoStale</c>). Only changes how the hold is
    /// reported: both hold the mailbox the same way.
    /// </summary>
    public bool IsRateLimited { get; }

    /// <summary>
    /// How long the server asked us to wait before trying again (<c>Retry-After</c>), when it sent
    /// one. Takes precedence over the queue's own retry schedule.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    public GraphDeliveryException(
        string message, bool isPermanent, Exception? innerException = null, string? errorCode = null,
        bool isThrottled = false, bool isRateLimited = false, TimeSpan? retryAfter = null)
        : base(message, innerException)
    {
        IsPermanent = isPermanent;
        ErrorCode = errorCode;
        IsThrottled = isThrottled;
        IsRateLimited = isRateLimited;
        RetryAfter = retryAfter;
    }
}
