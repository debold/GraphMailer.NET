namespace GraphMailer.Service.Configuration;

public sealed class GraphApiOptions
{
    public const string SectionName = "GraphApi";

    public string? TenantId { get; init; }
    public string? ClientId { get; init; }

    /// <summary>
    /// Sensitive – only set in config\graphmailer.json or via environment variable
    /// GRAPHMAILER_GRAPHAPI__CLIENTSECRET. Never put in appsettings.json.
    /// Mutually exclusive with ClientCertificateSubjectName; certificate takes precedence.
    /// </summary>
    public string? ClientSecret { get; init; }

    /// <summary>
    /// CN or SAN of the client certificate in the Windows Certificate Store (LocalMachine\My).
    /// Alternative to ClientCertificateThumbprint. When both are set, the thumbprint takes precedence.
    /// </summary>
    public string? ClientCertificateSubjectName { get; init; }

    /// <summary>
    /// Exact thumbprint (SHA-1 hex) of the client certificate.
    /// Recommended over SubjectName for Azure AD client-auth certificates because the certificate
    /// must be explicitly registered in the Entra app registration; only the exact cert is trusted.
    /// SubjectName auto-selects the newest certificate, which may not yet be registered in Entra.
    /// </summary>
    public string? ClientCertificateThumbprint { get; init; }

    /// <summary>
    /// Optional issuer filter when selecting by SubjectName and multiple certificates match.
    /// Not used when selecting by thumbprint.
    /// </summary>
    public string? ClientCertificateIssuer { get; init; }

    public bool HasClientSecret => !string.IsNullOrWhiteSpace(ClientSecret);
    public bool HasClientCertificate =>
        !string.IsNullOrWhiteSpace(ClientCertificateThumbprint) ||
        !string.IsNullOrWhiteSpace(ClientCertificateSubjectName);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TenantId) &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        (HasClientSecret || HasClientCertificate);
}
