namespace GraphMailer.Service.Configuration;

public sealed class CertificateOptions
{
    public const string SectionName = "Certificate";

    /// <summary>Windows Certificate Store location. Default: LocalMachine.</summary>
    public string StoreLocation { get; init; } = "LocalMachine";

    /// <summary>Windows Certificate Store name. Default: My (Personal).</summary>
    public string StoreName { get; init; } = "My";

    /// <summary>
    /// Subject name (CN or SAN DNS entry) to match, e.g. "smtp.example.com".
    /// Renewal-safe: new certificates with the same subject are picked up automatically.
    /// </summary>
    public string? SubjectName { get; init; }

    /// <summary>
    /// Optional issuer name to narrow the selection when multiple CAs issue certs
    /// for the same subject, e.g. "CN=My Internal CA".
    /// Only evaluated when SubjectName is also set.
    /// </summary>
    public string? Issuer { get; init; }

    /// <summary>
    /// When true, a TLS/STARTTLS listener whose certificate cannot be found is NOT
    /// started (fail-closed) instead of falling back to plain SMTP. Default false —
    /// the fallback keeps the relay operational during certificate rotation, at the
    /// cost of clients with opportunistic STARTTLS transmitting in cleartext.
    /// </summary>
    public bool FailClosed { get; init; }
}
