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
}
