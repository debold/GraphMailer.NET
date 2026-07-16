using System.Security.Cryptography.X509Certificates;

namespace GraphMailer.Service.Infrastructure.Certificates;

/// <summary>
/// Abstraction over certificate loading, allowing tests to inject
/// in-memory certificates without touching the Windows Certificate Store.
/// </summary>
internal interface ICertificateLoader
{
    /// <summary>Returns the configured certificate, or null if none is available.</summary>
    X509Certificate2? LoadCertificate();

    /// <summary>Returns true when a certificate selector has been configured.</summary>
    bool IsConfigured();
}
