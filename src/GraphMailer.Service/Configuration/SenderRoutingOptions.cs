namespace GraphMailer.Service.Configuration;

/// <summary>
/// Settings for sending as recipients that have no Exchange Online mailbox of their own —
/// distribution groups, mail-enabled public folders and mail users.
///
/// Graph's <c>/users/{key}/sendMail</c> only accepts a real mailbox as user key, so those
/// senders are delivered through a relay mailbox while the message keeps the original
/// address in its <c>From</c> header. Exchange authorises that pairing via the SendAs
/// permission, which has to be granted on each object to the relay mailbox:
/// <c>Add-RecipientPermission -Identity &lt;object&gt; -Trustee &lt;relay&gt; -AccessRights SendAs</c>.
/// </summary>
public sealed class SenderRoutingOptions
{
    public const string SectionName = "SenderRouting";

    public bool Enabled { get; init; } = false;

    /// <summary>
    /// UPN or primary SMTP address of a real mailbox used as the sending context.
    /// A shared mailbox is enough — app-only Mail.Send works without a licence.
    /// </summary>
    public string RelayMailbox { get; init; } = "";

    /// <summary>How long a learned "this sender has no mailbox" marker stays valid.</summary>
    public int RelayCacheMinutes { get; init; } = 60;

    /// <summary>
    /// Optional overrides for senders that need a specific relay mailbox, and for senders
    /// Graph cannot discover at all (mail-enabled public folders, dynamic distribution
    /// groups). Entries starting with '@' match a whole domain.
    /// </summary>
    public List<SenderRouteOptions> Routes { get; init; } = [];
}

/// <summary>One sender-to-relay-mailbox override.</summary>
public sealed class SenderRouteOptions
{
    /// <summary>Exact address or '@domain' wildcard, same semantics as the allow/block lists.</summary>
    public string Sender { get; init; } = "";

    /// <summary>UPN or primary SMTP address of the mailbox to send through.</summary>
    public string Mailbox { get; init; } = "";
}
