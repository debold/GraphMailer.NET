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

    public GraphDeliveryException(string message, bool isPermanent, Exception? innerException = null)
        : base(message, innerException)
    {
        IsPermanent = isPermanent;
    }
}
