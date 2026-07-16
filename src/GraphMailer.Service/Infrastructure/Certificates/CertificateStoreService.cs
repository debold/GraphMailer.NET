using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GraphMailer.Service.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphMailer.Service.Infrastructure.Certificates;

/// <summary>
/// Loads TLS certificates from the Windows Certificate Store.
/// Selection is based on SubjectName + optional Issuer (or FriendlyName as fallback).
/// When multiple certificates match, the one with the latest NotAfter is chosen,
/// which means renewed certificates are picked up automatically without any config change.
/// </summary>
internal sealed class CertificateStoreService(
    IOptions<CertificateOptions> options,
    ILogger<CertificateStoreService> logger) : ICertificateLoader
{
    private readonly CertificateOptions _options = options.Value;

    /// <summary>
    /// Loads the best matching certificate from the Windows store, or returns null
    /// if no certificate is configured or none could be found.
    /// </summary>
    public X509Certificate2? LoadCertificate()
    {
        if (!IsConfigured())
        {
            logger.LogDebug("[CertificateStore] No certificate selector configured – TLS disabled.");
            return null;
        }

        if (!Enum.TryParse<StoreLocation>(_options.StoreLocation, ignoreCase: true, out var storeLocation))
        {
            logger.LogError("[CertificateStore] Invalid StoreLocation value: {Value}. Use 'LocalMachine' or 'CurrentUser'.",
                _options.StoreLocation);
            return null;
        }

        if (!Enum.TryParse<StoreName>(_options.StoreName, ignoreCase: true, out var storeName))
        {
            logger.LogError("[CertificateStore] Invalid StoreName value: {Value}. Use 'My', 'WebHosting', etc.",
                _options.StoreName);
            return null;
        }

        using var store = new X509Store(storeName, storeLocation);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

        var candidates = store.Certificates
            .Where(c => c.NotBefore.ToUniversalTime() <= DateTime.UtcNow && c.NotAfter.ToUniversalTime() >= DateTime.UtcNow)
            .Where(c => c.HasPrivateKey)
            .Where(c => MatchesSelector(c))
            .OrderByDescending(c => c.NotAfter)
            .ToList();

        if (candidates.Count == 0)
        {
            logger.LogWarning("[CertificateStore] No valid certificate found in {Location}\\{Store} matching {Selector}.",
                _options.StoreLocation, _options.StoreName, BuildSelectorDescription());
            return null;
        }

        var chosen = candidates[0];

        // Verify the private key is accessible to the current process (service account).
        // If not, SslStream will fail with 0x80090327 during the TLS handshake.
        try
        {
            using var rsa = chosen.GetRSAPrivateKey();
            if (rsa != null) { rsa.ExportParameters(false); }
            else
            {
                using var ecdsa = chosen.GetECDsaPrivateKey();
                if (ecdsa != null) { ecdsa.ExportParameters(false); }
                else
                {
                    logger.LogError(
                        "[CertificateStore] Certificate '{Subject}' has no RSA or ECDSA private key. TLS will not work.",
                        chosen.Subject);
                    return null;
                }
            }
        }
        catch (CryptographicException ex)
        {
            logger.LogError(ex,
                "[CertificateStore] Private key for certificate '{Subject}' (Thumbprint: {Thumbprint}) is not accessible " +
                "to the service account. Grant read access to the key container or import the certificate with " +
                "'Allow all applications to access this key' enabled. ({Error})",
                chosen.Subject, chosen.Thumbprint, ex.Message);
            return null;
        }
        logger.LogDebug(
            "[CertificateStore] Selected certificate: Subject={Subject}, Issuer={Issuer}, Expires={Expires:yyyy-MM-dd}, Thumbprint={Thumbprint}",
            chosen.Subject, chosen.Issuer, chosen.NotAfter, chosen.Thumbprint);

        if (candidates.Count > 1)
        {
            logger.LogDebug("[CertificateStore] {Count} matching certificates found; chose the one with latest expiry.",
                candidates.Count);
        }

        return chosen;
    }

    /// <summary>Returns true when SubjectName is configured.</summary>
    public bool IsConfigured() =>
        !string.IsNullOrWhiteSpace(_options.SubjectName);

    // -------------------------------------------------------------------------

    private bool MatchesSelector(X509Certificate2 cert)
    {
        if (string.IsNullOrWhiteSpace(_options.SubjectName))
            return false;

        if (!SubjectMatches(cert, _options.SubjectName))
            return false;

        if (!string.IsNullOrWhiteSpace(_options.Issuer))
            return cert.Issuer.Contains(_options.Issuer, StringComparison.OrdinalIgnoreCase);

        return true;
    }

    private static bool SubjectMatches(X509Certificate2 cert, string subjectName)
    {
        // Check CN in Subject distinguished name
        if (cert.Subject.Contains($"CN={subjectName}", StringComparison.OrdinalIgnoreCase))
            return true;

        // Check Subject Alternative Names (DNS entries)
        var sanExtension = cert.Extensions["2.5.29.17"]; // OID for Subject Alternative Name
        if (sanExtension is null)
            return false;

        var sanText = sanExtension.Format(multiLine: false);
        // Parse individual entries to prevent partial-hostname false positives.
        // e.g. searching for "example.com" must NOT match "notexample.com".
        // SAN format: "DNS Name=smtp.example.com, DNS Name=mail.example.com, ..."
        return sanText.Split(',', StringSplitOptions.TrimEntries)
            .Any(e => e.Equals($"DNS Name={subjectName}", StringComparison.OrdinalIgnoreCase));
    }

    private string BuildSelectorDescription()
    {
        var desc = $"SubjectName='{_options.SubjectName}'";
        if (!string.IsNullOrWhiteSpace(_options.Issuer))
            desc += $" + Issuer='{_options.Issuer}'";
        return desc;
    }
}
