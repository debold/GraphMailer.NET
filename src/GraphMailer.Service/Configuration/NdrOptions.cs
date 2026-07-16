namespace GraphMailer.Service.Configuration;

public sealed class NdrOptions
{
    public const string SectionName = "NdrNotifications";

    public bool Enabled { get; init; } = false;

    /// <summary>Send an NDR email to the original SMTP sender when delivery permanently fails.</summary>
    public bool NotifySender { get; init; } = true;

    /// <summary>Send a copy of the NDR to the admin recipient addresses configured in AdminNotifications.</summary>
    public bool NotifyAdmin { get; init; } = false;
}
