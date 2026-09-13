namespace GraphMailer.Service.Services.Reporting;

/// <summary>
/// Builds the subject line of every mail GraphMailer sends on its own behalf: the configured
/// prefix (<c>AdminNotifications.SubjectPrefix</c>) followed by the message's own subject.
/// Shared by the service's admin notifications, NDR admin copies and scheduled reports and by
/// the ConfigTool's Graph API test mail, so all of them match the same inbox rules.
/// </summary>
internal static class NotificationSubject
{
    /// <summary>
    /// <c>"[Prefix] Subject"</c>. A missing or whitespace-only prefix yields the bare subject
    /// instead of a leading space.
    /// </summary>
    internal static string Build(string? prefix, string subject)
    {
        var trimmed = prefix?.Trim();
        return string.IsNullOrEmpty(trimmed) ? subject : $"{trimmed} {subject}";
    }
}
