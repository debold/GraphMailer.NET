using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using GraphMailer.Service.Infrastructure.Config;

namespace GraphMailer.Service.Infrastructure.Certificates;

/// <summary>
/// Creates and installs the self-signed TLS certificate used by the SMTP listeners when no
/// real certificate is configured. Shared by the service's first-run provisioner and the
/// ConfigTool's "Create self-signed certificate" button so the two never diverge.
/// </summary>
internal static class SelfSignedSmtpCertificate
{
    /// <summary>Result of <see cref="CreateAndInstall"/>.</summary>
    /// <param name="Subject">The certificate's simple subject name (CN).</param>
    /// <param name="AclGranted">
    /// True when NETWORK SERVICE was granted read access to the private key container.
    /// </param>
    internal readonly record struct Result(string Subject, bool AclGranted);

    /// <summary>
    /// Creates a self-signed RSA-2048 certificate for SMTP Server Authentication
    /// (CN = <see cref="DefaultConfiguration.SelfSignedSubjectName"/>), installs it into
    /// <c>LocalMachine\My</c> (with private key) and <c>LocalMachine\Root</c> (public key only,
    /// so clients on this machine trust it), and tries to grant NETWORK SERVICE read access to
    /// the private key container.
    /// </summary>
    /// <remarks>
    /// Requires write access to the LocalMachine certificate stores (elevated process / service
    /// account). The caller is responsible for handling the exception thrown when that is not
    /// the case.
    /// </remarks>
    internal static Result CreateAndInstall()
    {
        // GraphMailer ships win-x64 only; the LocalMachine store, ACLs and FriendlyName are
        // Windows-only. The guard also tells the platform-compatibility analyzer (CA1416) that
        // everything below runs on Windows.
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The SMTP certificate store is Windows-only.");

        using var temp = CreateSelfSigned();

        var pfx = temp.Export(X509ContentType.Pfx, (string?)null);
        using var persistent = new X509Certificate2(
            pfx, (string?)null,
            X509KeyStorageFlags.MachineKeySet |
            X509KeyStorageFlags.PersistKeySet |
            X509KeyStorageFlags.Exportable);
        persistent.FriendlyName = "GraphMailer SMTP (self-signed)";

        // Try to grant NETWORK SERVICE read access to the CNG key container file so a service
        // running as NETWORK SERVICE can use the cert without manual ACL configuration. CNG
        // machine keys are stored as files under %ALLUSERSPROFILE%\Microsoft\Crypto\Keys\.
        // (LocalSystem already has access; this only matters for NETWORK SERVICE deployments.)
        bool aclGranted = false;
        try
        {
            if (persistent.GetRSAPrivateKey() is RSACng rsaCng)
            {
                var keyName = rsaCng.Key.UniqueName;
                if (keyName is not null)
                {
                    var keyDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "Microsoft", "Crypto", "Keys");
                    var keyFile = Path.Combine(keyDir, keyName);
                    if (File.Exists(keyFile))
                    {
                        var fi = new FileSecurity(keyFile, AccessControlSections.Access);
                        fi.AddAccessRule(new FileSystemAccessRule(
                            new NTAccount("NT AUTHORITY", "NETWORK SERVICE"),
                            FileSystemRights.Read,
                            AccessControlType.Allow));
                        FileSystemAclExtensions.SetAccessControl(new FileInfo(keyFile), fi);
                        aclGranted = true;
                    }
                }
            }
        }
        catch { /* Non-critical — user can set ACLs manually via certlm.msc */ }

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(persistent);

        // Install the public cert (no private key) into Trusted Root CA so that SMTP clients on
        // this machine accept the self-signed certificate without error 0x80090325
        // "untrusted root".
        using var rootStore = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        rootStore.Open(OpenFlags.ReadWrite);
        using var publicOnly = new X509Certificate2(persistent.RawData); // strips private key
        rootStore.Add(publicOnly);

        return new Result(persistent.GetNameInfo(X509NameType.SimpleName, false), aclGranted);
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
