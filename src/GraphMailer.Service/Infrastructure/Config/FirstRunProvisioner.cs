using System.Security.Cryptography.X509Certificates;
using GraphMailer.Service.Infrastructure.Certificates;
using Microsoft.AspNetCore.DataProtection;
using Serilog;

namespace GraphMailer.Service.Infrastructure.Config;

/// <summary>
/// First-run provisioning: when no <c>graphmailer.json</c> exists yet (fresh install), seed it
/// with the array-shaped defaults from <see cref="DefaultConfiguration"/> (the SMTP listeners and
/// the private-range IP whitelist) and, when TLS listeners are present, generate + bind a
/// self-signed certificate so the encrypted connectors work out of the box.
///
/// <para>
/// Runs before the configuration is loaded (mirrors <c>ConfigMigrator.MigrateFile</c>), so the
/// seeded values are picked up by the normal config pipeline. Seeding the arrays into
/// <c>graphmailer.json</c> — rather than shipping them in <c>appsettings.json</c> — avoids the
/// IConfiguration index-merge pitfall (see <see cref="DefaultConfiguration"/>).
/// </para>
/// </summary>
internal static class FirstRunProvisioner
{
    /// <summary>
    /// Seeds <paramref name="configFilePath"/> when it does not exist yet. No-op otherwise.
    /// Never throws: provisioning failures are logged and the service starts on the bundled
    /// defaults (TLS connectors then fall back to plain with an error log until a cert is set).
    /// </summary>
    internal static void EnsureProvisioned(string configFilePath, IDataProtector configProtector)
        => EnsureProvisioned(configFilePath, configProtector, EnsureSelfSignedCertificate);

    /// <summary>
    /// Testable core: <paramref name="provisionCert"/> supplies the certificate subject to bind
    /// (or null when none/unavailable), so tests can seed without touching the machine store.
    /// </summary>
    internal static void EnsureProvisioned(
        string configFilePath, IDataProtector configProtector, Func<string?> provisionCert)
    {
        if (File.Exists(configFilePath))
            return;

        try
        {
            var doc = new ConfigDocument
            {
                Servers = DefaultConfiguration.Servers(),
            };
            doc.Access.IpWhitelist = DefaultConfiguration.IpWhitelist();
            doc.Access.IpWhitelistComments = DefaultConfiguration.IpWhitelistComments();

            // Provision a TLS certificate only when an encrypted listener actually needs one.
            bool needsTls = doc.Servers.Any(s =>
                s.Mode.Equals("Tls", StringComparison.OrdinalIgnoreCase) ||
                s.Mode.Equals("StartTls", StringComparison.OrdinalIgnoreCase));

            if (needsTls)
                doc.Certificate.SubjectName = provisionCert();

            new ConfigService(configFilePath, configProtector).Save(doc);

            Log.Information(
                "[FirstRun] Seeded default configuration at {Path} ({Listeners} listener(s), {Whitelist} whitelist entries, TLS cert: {Cert})",
                configFilePath, doc.Servers.Count, doc.Access.IpWhitelist.Count,
                doc.Certificate.SubjectName ?? "none");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[FirstRun] Could not seed default configuration – starting on bundled defaults");
        }
    }

    /// <summary>
    /// Returns the subject of a usable self-signed SMTP certificate, generating and installing
    /// one if none exists yet. Returns null when generation fails (e.g. non-elevated dev run),
    /// leaving TLS unbound so the runtime plain-SMTP fallback applies.
    /// </summary>
    private static string? EnsureSelfSignedCertificate()
    {
        if (SelfSignedCertExists())
        {
            Log.Information("[FirstRun] Reusing existing '{Subject}' certificate from LocalMachine\\My",
                DefaultConfiguration.SelfSignedSubjectName);
            return DefaultConfiguration.SelfSignedSubjectName;
        }

        try
        {
            var result = SelfSignedSmtpCertificate.CreateAndInstall();
            Log.Information(
                "[FirstRun] Created self-signed SMTP certificate '{Subject}' (NETWORK SERVICE key access: {Acl})",
                result.Subject, result.AclGranted ? "granted" : "not granted");
            return result.Subject;
        }
        catch (Exception ex)
        {
            Log.Error(ex,
                "[FirstRun] Could not create the self-signed SMTP certificate – TLS listeners will fall back to plain SMTP until a certificate is configured");
            return null;
        }
    }

    private static bool SelfSignedCertExists()
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            return store.Certificates.Any(c =>
                c.HasPrivateKey &&
                c.NotAfter.ToUniversalTime() >= DateTime.UtcNow &&
                c.GetNameInfo(X509NameType.SimpleName, false)
                 .Equals(DefaultConfiguration.SelfSignedSubjectName, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }
}
