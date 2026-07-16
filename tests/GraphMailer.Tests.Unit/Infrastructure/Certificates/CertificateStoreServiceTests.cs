using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GraphMailer.Tests.Unit.Infrastructure.Certificates;

public sealed class CertificateStoreServiceTests
{
    // -------------------------------------------------------------------------
    // IsConfigured
    // -------------------------------------------------------------------------

    [Fact]
    public void IsConfigured_NoSelector_ReturnsFalse()
    {
        var sut = BuildSut(new CertificateOptions());
        sut.IsConfigured().Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_WithSubjectName_ReturnsTrue()
    {
        var sut = BuildSut(new CertificateOptions { SubjectName = "smtp.example.com" });
        sut.IsConfigured().Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // LoadCertificate – no selector
    // -------------------------------------------------------------------------

    [Fact]
    public void LoadCertificate_NoSelector_ReturnsNull()
    {
        var sut = BuildSut(new CertificateOptions());
        sut.LoadCertificate().Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // LoadCertificate – invalid store config
    // -------------------------------------------------------------------------

    [Fact]
    public void LoadCertificate_InvalidStoreLocation_ReturnsNull()
    {
        var sut = BuildSut(new CertificateOptions
        {
            SubjectName = "smtp.example.com",
            StoreLocation = "InvalidLocation"
        });
        sut.LoadCertificate().Should().BeNull();
    }

    [Fact]
    public void LoadCertificate_InvalidStoreName_ReturnsNull()
    {
        var sut = BuildSut(new CertificateOptions
        {
            SubjectName = "smtp.example.com",
            StoreName = "InvalidStoreName"
        });
        sut.LoadCertificate().Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // LoadCertificate – round-trip with real cert in CurrentUser store
    // (uses a self-signed cert installed and removed within the test)
    // -------------------------------------------------------------------------

    [Fact]
    public void LoadCertificate_SubjectMatch_ReturnsCert()
    {
        const string subjectName = "graphmailer-test-subject.local";
        using var cert = CreateSelfSignedCert($"CN={subjectName}");

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(cert);

        try
        {
            var sut = BuildSut(new CertificateOptions
            {
                StoreLocation = "CurrentUser",
                StoreName = "My",
                SubjectName = subjectName
            });

            var result = sut.LoadCertificate();
            result.Should().NotBeNull();
            result!.Subject.Should().Contain(subjectName);
        }
        finally
        {
            store.Remove(cert);
            store.Close();
            DeletePersistedKey(cert);
        }
    }

    [Fact]
    public void LoadCertificate_SubjectAndIssuerMatch_ReturnsCert()
    {
        const string subjectName = "graphmailer-test-issuer.local";
        using var cert = CreateSelfSignedCert($"CN={subjectName}");

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(cert);

        try
        {
            // The self-signed cert is its own issuer
            var sut = BuildSut(new CertificateOptions
            {
                StoreLocation = "CurrentUser",
                StoreName = "My",
                SubjectName = subjectName,
                Issuer = subjectName   // self-signed: issuer == subject
            });

            var result = sut.LoadCertificate();
            result.Should().NotBeNull();
        }
        finally
        {
            store.Remove(cert);
            store.Close();
            DeletePersistedKey(cert);
        }
    }

    [Fact]
    public void LoadCertificate_IssuerMismatch_ReturnsNull()
    {
        const string subjectName = "graphmailer-test-issuermismatch.local";
        using var cert = CreateSelfSignedCert($"CN={subjectName}");

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(cert);

        try
        {
            var sut = BuildSut(new CertificateOptions
            {
                StoreLocation = "CurrentUser",
                StoreName = "My",
                SubjectName = subjectName,
                Issuer = "CN=Some Other CA"   // won't match self-signed cert
            });

            sut.LoadCertificate().Should().BeNull();
        }
        finally
        {
            store.Remove(cert);
            store.Close();
            DeletePersistedKey(cert);
        }
    }

    [Fact]
    public void LoadCertificate_MultipleMatches_ReturnsLatestExpiry()
    {
        const string subjectName = "graphmailer-test-multi.local";

        using var certOld = CreateSelfSignedCert($"CN={subjectName}", validDays: 30);
        using var certNew = CreateSelfSignedCert($"CN={subjectName}", validDays: 365);

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certOld);
        store.Add(certNew);

        try
        {
            var sut = BuildSut(new CertificateOptions
            {
                StoreLocation = "CurrentUser",
                StoreName = "My",
                SubjectName = subjectName
            });

            var result = sut.LoadCertificate();
            result.Should().NotBeNull();
            result!.NotAfter.Should().BeCloseTo(certNew.NotAfter, TimeSpan.FromSeconds(5));
        }
        finally
        {
            store.Remove(certOld);
            store.Remove(certNew);
            store.Close();
            DeletePersistedKey(certOld);
            DeletePersistedKey(certNew);
        }
    }

    [Fact]
    public void LoadCertificate_SubjectNotFound_ReturnsNull()
    {
        var sut = BuildSut(new CertificateOptions
        {
            StoreLocation = "CurrentUser",
            StoreName = "My",
            SubjectName = "this-subject-does-not-exist-in-any-store.invalid"
        });

        sut.LoadCertificate().Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static CertificateStoreService BuildSut(CertificateOptions opts) =>
        new(Options.Create(opts), NullLogger<CertificateStoreService>.Instance);

    /// <summary>
    /// Creates a self-signed test certificate whose private key is backed by a real
    /// Windows Key Storage Provider (KSP) container.
    ///
    /// RSA.Create() produces an ephemeral CNG key that lives only in memory.  When such
    /// a certificate is added to an X509Store and then read back from a *new* store
    /// instance, Windows cannot locate the private key → HasPrivateKey = false →
    /// CertificateStoreService.LoadCertificate() filters the cert out.
    ///
    /// Exporting to PFX and reimporting with PersistKeySet writes the private key into
    /// a real KSP container so that HasPrivateKey remains true across store boundaries.
    /// Call <see cref="DeletePersistedKey"/> in the finally block to clean up the
    /// KSP container after the test.
    /// </summary>
    private static X509Certificate2 CreateSelfSignedCert(string subjectName, int validDays = 90)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subjectName, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.UtcNow;
        using var ephemeral = request.CreateSelfSigned(now.AddMinutes(-1), now.AddDays(validDays));

        // Export to PFX then reimport with PersistKeySet so the private key gets a real
        // KSP backing file.  Without this, HasPrivateKey = false in new store instances.
        var pfx = ephemeral.Export(X509ContentType.Pfx)!;
        return new X509Certificate2(pfx, (string?)null,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
    }

    /// <summary>
    /// Deletes the persisted KSP private-key container for <paramref name="cert"/>.
    /// Must be called in each test's finally block after store.Remove() so the key
    /// file in %AppData%\Microsoft\Crypto\RSA\ does not accumulate across test runs.
    /// </summary>
    private static void DeletePersistedKey(X509Certificate2 cert)
    {
        try
        {
            using var rsa = cert.GetRSAPrivateKey();
            if (rsa is RSACng cng)
                cng.Key.Delete();
        }
        catch { /* best-effort: key may already be gone */ }
    }
}
