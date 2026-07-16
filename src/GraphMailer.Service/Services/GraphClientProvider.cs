using System.Security.Cryptography.X509Certificates;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Extensions.Logging;
using GraphMailer.Service.Configuration;

namespace GraphMailer.Service.Services;

/// <summary>
/// Creates and caches the GraphServiceClient shared by all Graph consumers
/// (GraphApiClient for mail delivery, GraphDirectoryGateway for sender lookups).
/// The client is re-created when tenant, client id or auth method change.
/// </summary>
internal sealed class GraphClientProvider
{
    private readonly ILogger<GraphClientProvider> _logger;

    private readonly object _clientLock = new();
    private GraphServiceClient? _graphClient;
    private string? _currentConfigKey;

    public GraphClientProvider(ILogger<GraphClientProvider> logger)
    {
        _logger = logger;
    }

    public GraphServiceClient GetClient(GraphApiOptions opts)
    {
        // The config key must change whenever the CREDENTIAL changes, not only the
        // identity: rotating the client secret (or switching the configured
        // certificate) via config reload must rebuild the cached client — otherwise
        // the stale credential keeps failing until a service restart.
        // Certificate selection by SubjectName (auto-latest) still requires a restart
        // to pick up a newly installed cert, since the store is only read on rebuild.
        var authMethod = opts.HasClientCertificate ? "cert" : "secret";
        var credentialFingerprint = opts.HasClientCertificate
            ? $"{opts.ClientCertificateThumbprint}|{opts.ClientCertificateSubjectName}|{opts.ClientCertificateIssuer}"
            : HashSecret(opts.ClientSecret);
        var configKey = $"{opts.TenantId}:{opts.ClientId}:{authMethod}:{credentialFingerprint}";

        lock (_clientLock)
        {
            if (_graphClient != null && _currentConfigKey == configKey)
                return _graphClient;

            _graphClient = new GraphServiceClient(CreateCredential(opts));
            _currentConfigKey = configKey;
            return _graphClient;
        }
    }

    // The raw secret never becomes part of the cache key — a hash detects rotation
    // without keeping another plaintext copy reachable via the provider's state.
    private static string HashSecret(string? secret)
        => string.IsNullOrEmpty(secret)
            ? "none"
            : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(secret)));

    /// <summary>
    /// Creates a fresh, uncached credential for the configured auth method.
    /// Used by <see cref="GetClient"/> and by the connectivity probe — the probe
    /// needs a new instance per check so MSAL's token cache cannot mask an outage.
    /// </summary>
    public Azure.Core.TokenCredential CreateCredential(GraphApiOptions opts)
    {
        if (opts.HasClientCertificate)
        {
            var cert = LoadClientCertificate(opts);
            if (cert is null)
            {
                var selector = !string.IsNullOrWhiteSpace(opts.ClientCertificateThumbprint)
                    ? $"Thumbprint='{opts.ClientCertificateThumbprint}'"
                    : $"SubjectName='{opts.ClientCertificateSubjectName}'";
                throw new InvalidOperationException(
                    $"[GraphApi] Certificate ({selector}) not found in LocalMachine\\My or CurrentUser\\My.");
            }
            _logger.LogInformation(
                "[GraphApi] Initializing Graph API credential with certificate auth " +
                "(tenant: {TenantId}, subject: {Subject}, expires: {Expires:yyyy-MM-dd})",
                opts.TenantId, cert.Subject, cert.NotAfter);

            return new ClientCertificateCredential(opts.TenantId!, opts.ClientId!, cert);
        }

        _logger.LogInformation(
            "[GraphApi] Initializing Graph API credential with client secret " +
            "(tenant: {TenantId}, clientId: {ClientId})",
            opts.TenantId, opts.ClientId);

        return new ClientSecretCredential(opts.TenantId!, opts.ClientId!, opts.ClientSecret!);
    }

    /// <summary>
    /// Loads the client certificate from the Windows Certificate Store.
    ///
    /// Selection order:
    ///   1. Thumbprint (exact match) – recommended for Azure AD client-auth certs because
    ///      the cert must be explicitly registered in Entra; only the exact cert is trusted.
    ///   2. SubjectName + optional Issuer (latest NotAfter) – useful for zero-downtime
    ///      rotation where both old and new certs are pre-registered in Entra.
    ///
    /// Searches LocalMachine\My first, then CurrentUser\My (for development).
    /// </summary>
    private static X509Certificate2? LoadClientCertificate(GraphApiOptions opts)
    {
        foreach (var (location, name) in new[]
        {
            (StoreLocation.LocalMachine, StoreName.My),
            (StoreLocation.CurrentUser,  StoreName.My)
        })
        {
            using var store = new X509Store(name, location);
            try { store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly); }
            catch { continue; }

            // --- Path 1: exact thumbprint match (recommended for Azure AD client certs) ---
            if (!string.IsNullOrWhiteSpace(opts.ClientCertificateThumbprint))
            {
                var thumbprint = opts.ClientCertificateThumbprint.Replace(" ", "").ToUpperInvariant();
                var byThumbprint = store.Certificates
                    .Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false)
                    .FirstOrDefault();

                if (byThumbprint is not null)
                    return byThumbprint;

                continue; // thumbprint configured but not in this store → try next store
            }

            // --- Path 2: SubjectName + optional Issuer (auto-selects newest matching cert) ---
            var now = DateTime.UtcNow;
            var match = store.Certificates
                .Where(c => c.NotBefore.ToUniversalTime() <= now && c.NotAfter.ToUniversalTime() >= now)
                .Where(c => CertMatchesSubject(c, opts.ClientCertificateSubjectName!, opts.ClientCertificateIssuer))
                .OrderByDescending(c => c.NotAfter)
                .FirstOrDefault();

            if (match is not null)
                return match;
        }

        return null;
    }

    private static bool CertMatchesSubject(X509Certificate2 cert, string subjectName, string? issuerFilter)
    {
        bool subjectMatch =
            cert.Subject.Contains($"CN={subjectName}", StringComparison.OrdinalIgnoreCase) ||
            HasSanEntry(cert, subjectName);

        if (!subjectMatch) return false;

        return string.IsNullOrWhiteSpace(issuerFilter) ||
               cert.Issuer.Contains(issuerFilter, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSanEntry(X509Certificate2 cert, string subjectName)
    {
        var san = cert.Extensions["2.5.29.17"];
        if (san is null) return false;

        return san.Format(multiLine: false)
            .Split(',', StringSplitOptions.TrimEntries)
            .Any(e => e.Equals($"DNS Name={subjectName}", StringComparison.OrdinalIgnoreCase));
    }
}
