namespace GraphMailer.ConfigTool.Helpers;

/// <summary>
/// The line under the Messages list that explains why it is empty or incomplete. An empty list
/// must never look like "no mail was sent" when the real reason is that nothing could be read or
/// nothing was recorded.
/// </summary>
internal static class MessageListHint
{
    /// <summary>The hint to show, or null when the list speaks for itself.</summary>
    internal static string? Build(
        int rowCount, bool searching, MessageListFilter filter, bool archiveSent,
        bool metricsEnabled, bool databaseExists, string? readError)
    {
        // A read error matters even with rows on screen: the list is then incomplete.
        if (readError is not null)
            return $"The message history in metrics.db could not be read, so the list may be incomplete: {readError}";

        if (rowCount > 0 || searching) return null;

        if (!metricsEnabled)
            return "Metrics are switched off (Monitoring page), so GraphMailer records no message history. " +
                   "Only messages still in the mail folders are listed" +
                   (archiveSent ? "." : ", and delivered mail is not archived (Mail Queue page).");

        if (!databaseExists)
            return "No message history recorded yet. The service writes it to metrics.db as soon as it handles a message.";

        return filter switch
        {
            MessageListFilter.All => "No messages recorded yet. Messages appear here once the service has handled them; " +
                                     "their history is kept for the Metrics retention period.",
            MessageListFilter.Sent when !archiveSent =>
                "No delivered messages recorded yet. Delivered messages appear here once the service " +
                "has handled them (history is kept for the Metrics retention period).",
            _ => null,
        };
    }
}
