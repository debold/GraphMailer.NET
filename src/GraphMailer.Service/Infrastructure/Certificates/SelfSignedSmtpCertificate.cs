using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GraphMailer.Service.Infrastructure.Config;

namespace GraphMailer.Service.Infrastructure.Certificates;

/// <summary>
/// Creates and installs the self-signed TLS certificate used by the SMTP listeners when no
/// real certificate is configured. Shared by the service's first-run provisioner and the
/// ConfigTool's "Create self-signed certificate" button so the two never diverge.
/// </summary>
internal static class SelfSignedSmtpCertificate
{
    /// <summary>
    /// Creates a self-signed RSA-2048 certificate for SMTP Server Authentication
    /// (CN = <see cref="DefaultConfiguration.SelfSignedSubjectName"/>) and installs it into
    /// <c>LocalMachine\My</c> (with private key) and <c>LocalMachine\Root</c> (public key only,
    /// so clients on this machine trust it).
    /// </summary>
    /// <returns>The certificate's simple subject name (CN).</returns>
    /// <remarks>
    /// <para>
    /// Requires write access to the LocalMachine certificate stores (elevated process / service
    /// account). The caller is responsible for handling the exception thrown when that is not
    /// the case.
    /// </para>
    /// <para>
    /// The private key keeps the default machine-key ACL (SYSTEM and Administrators), which is
    /// exactly what the service needs: both install paths register it as LocalSystem. Earlier
    /// versions also granted NETWORK SERVICE read access; that let every other service running
    /// under that account read the key of a certificate this machine trusts as a root, so it is
    /// no longer done.
    /// </para>
    /// </remarks>
    internal static string CreateAndInstall()
    {
        // GraphMailer ships win-x64 only; the LocalMachine store and FriendlyName are
        // Windows-only. The guard also tells the platform-compatibility analyzer (CA1416) that
        // everything below runs on Windows.
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The SMTP certificate store is Windows-only.");

        using var temp = CreateSelfSigned();

        var pfx = temp.Export(X509ContentType.Pfx, (string?)null);
        using var persistent = X509CertificateLoader.LoadPkcs12(
            pfx, (string?)null,
            X509KeyStorageFlags.MachineKeySet |
            X509KeyStorageFlags.PersistKeySet |
            X509KeyStorageFlags.Exportable);
        persistent.FriendlyName = "GraphMailer SMTP (self-signed)";

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(persistent);

        // Install the public cert (no private key) into Trusted Root CA so that SMTP clients on
        // this machine accept the self-signed certificate without error 0x80090325
        // "untrusted root".
        using var rootStore = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        rootStore.Open(OpenFlags.ReadWrite);
        using var publicOnly = X509CertificateLoader.LoadCertificate(persistent.RawData); // strips private key
        rootStore.Add(publicOnly);

        return persistent.GetNameInfo(X509NameType.SimpleName, false);
    }

    /// <summary>
    /// Builds the in-memory self-signed SMTP certificate (RSA-2048, Server-Auth EKU, SAN =
    /// localhost + machine hostname, 10-year validity). No store writes / key persistence, so
    /// it is unit-testable without elevation. <see cref="CreateAndInstall"/> persists it.
    /// </summary>
    internal static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            $"CN={DefaultConfiguration.SelfSignedSubjectName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, critical: true));
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: false));
        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false)); // Server Authentication
        req.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(req.PublicKey, critical: false));

        // SAN: localhost + machine hostname (SMTP clients may connect by machine name)
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        req.CertificateExtensions.Add(san.Build());

        return req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(10));
    }
}
