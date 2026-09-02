namespace GraphMailer.Service.Configuration;

/// <summary>
/// Settings for validating SMTP MAIL FROM addresses against the Microsoft 365
/// tenant's known sender addresses (users incl. aliases / proxyAddresses).
/// Requires the User.Read.All application permission on the Entra app registration.
/// </summary>
public sealed class SenderValidationOptions
{
    public const string SectionName = "SenderValidation";

    public bool Enabled { get; init; } = false;

    /// <summary>Interval of the periodic full directory sync.</summary>
    public int RefreshIntervalMinutes { get; init; } = 60;

    /// <summary>How long a "sender not found" result is cached before Graph is asked again.</summary>
    public int NegativeCacheSeconds { get; init; } = 300;

    /// <summary>Maximum time a single on-demand Graph lookup may take during MAIL FROM.</summary>
    public int LookupTimeoutSeconds { get; init; } = 5;

    /// <summary>
    /// When true, senders are rejected if validation is impossible (Graph unreachable,
    /// permission missing, cache never synced). Default false = fail-open: accept.
    /// </summary>
    public bool FailClosed { get; init; } = false;

    /// <summary>
    /// Accept senders that own no mailbox of their own, in two steps:
    ///
    ///   • mail-enabled groups (distribution groups, mail-enabled security groups, Microsoft 365
    ///     groups) are synced into the directory and matched by address;
    ///   • mail-enabled public folders and dynamic distribution groups do not exist in Graph at
    ///     all, so an address is accepted when its domain is one the tenant itself uses.
    ///
    /// That domain rule is weaker than an address-exact check — a made-up address in an own
    /// domain passes too — but every external sender is still rejected. The domains are derived
    /// from the directory that is already loaded (see TenantSenderDirectory), so this needs only
    /// the additional Group.Read.All application permission.
    /// </summary>
    public bool AcceptMailboxlessSenders { get; init; } = false;
}
