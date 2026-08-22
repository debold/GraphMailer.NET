using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GraphMailer.Service.Infrastructure.Certificates;

namespace GraphMailer.Tests.Unit.Infrastructure.Certificates;

/// <summary>
/// Which certificate the selector accepts for a configured SubjectName / Issuer.
///
/// The subject used to be matched with <c>Subject.Contains("CN=" + name)</c>. A distinguished
/// name reads "CN=mail.contoso.com, O=Contoso", so that test anchors the start of the value and
/// nothing anchors the end: <c>CN=mail.contoso.com.attacker.net</c> and
/// <c>CN=mail.contoso.community</c> both passed. Placing such a certificate needs write access to
/// the machine store, so this was never a remote attack — but "newest NotAfter wins" then picks
/// the wrong certificate silently, and the rule for SAN matching (exact, never Contains) applies
/// just as much to the CN. Every one of those look-alikes gets a test here.
/// </summary>
public sealed class CertificateSelectorMatchingTests
{
    private const string Wanted = "mail.contoso.com";

    private static X509Certificate2 Cert(string subjectDn, string? issuerDn = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subjectDn, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.UtcNow;

        if (issuerDn is null)
            return request.CreateSelfSigned(now.AddMinutes(-1), now.AddDays(30));

        // Sign with a separate CA so Subject and Issuer differ.
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest(issuerDn, caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(365));

        return request.Create(ca, now.AddMinutes(-1), now.AddDays(30), Guid.NewGuid().ToByteArray()[..8]);
    }

    // ── Subject: the exact name is still accepted ────────────────────────────

    [Fact]
    public void SubjectMatches_ExactCommonName_IsAccepted()
    {
        using var cert = Cert($"CN={Wanted}, O=Contoso, C=DE");
        CertificateStoreService.SubjectMatches(cert, Wanted).Should().BeTrue();
    }

    [Fact]
    public void SubjectMatches_CasingIsIgnored()
    {
        using var cert = Cert($"CN={Wanted}");
        CertificateStoreService.SubjectMatches(cert, "MAIL.CONTOSO.COM").Should().BeTrue();
    }

    // ── Subject: the look-alikes the substring test used to accept ───────────

    [Fact]
    public void SubjectMatches_NameExtendedToAnotherDomain_IsRejected()
    {
        // The regression case: "CN=mail.contoso.com.attacker.net" contains "CN=mail.contoso.com".
        using var cert = Cert("CN=mail.contoso.com.attacker.net, O=Evil");
        CertificateStoreService.SubjectMatches(cert, Wanted).Should().BeFalse();
    }

    [Fact]
    public void SubjectMatches_LongerTldOnTheSameLabel_IsRejected()
    {
        // "mail.contoso.community" also starts with "mail.contoso.com".
        using var cert = Cert("CN=mail.contoso.community");
        CertificateStoreService.SubjectMatches(cert, Wanted).Should().BeFalse();
    }

    [Fact]
    public void SubjectMatches_PrefixedName_IsRejected()
    {
        // This one the old substring test already rejected — kept so the fix cannot regress it.
        using var cert = Cert("CN=notmail.contoso.com");
        CertificateStoreService.SubjectMatches(cert, Wanted).Should().BeFalse();
    }

    [Fact]
    public void SubjectMatches_NameOnlyInAnotherAttribute_IsRejected()
    {
        // The wanted name appears in the DN, but as the organisation — not as a CN.
        using var cert = Cert($"CN=something.else.test, O={Wanted}");
        CertificateStoreService.SubjectMatches(cert, Wanted).Should().BeFalse();
    }

    // ── Issuer ───────────────────────────────────────────────────────────────

    [Fact]
    public void IssuerMatches_ConfiguredAsDnFragment_IsAccepted()
    {
        // The form the option documents: "CN=My Internal CA".
        using var cert = Cert($"CN={Wanted}", issuerDn: "CN=My Internal CA, O=Contoso");
        CertificateStoreService.IssuerMatches(cert, "CN=My Internal CA").Should().BeTrue();
    }

    [Fact]
    public void IssuerMatches_ConfiguredAsBareName_IsAccepted()
    {
        // The form people actually type.
        using var cert = Cert($"CN={Wanted}", issuerDn: "CN=My Internal CA, O=Contoso");
        CertificateStoreService.IssuerMatches(cert, "My Internal CA").Should().BeTrue();
    }

    [Fact]
    public void IssuerMatches_PartialCaName_IsRejected()
    {
        // Deliberate tightening: "Internal" used to hit "CN=My Internal CA" through Contains.
        using var cert = Cert($"CN={Wanted}", issuerDn: "CN=My Internal CA, O=Contoso");
        CertificateStoreService.IssuerMatches(cert, "Internal").Should().BeFalse();
    }

    [Fact]
    public void IssuerMatches_DifferentCa_IsRejected()
    {
        using var cert = Cert($"CN={Wanted}", issuerDn: "CN=Someone Elses CA");
        CertificateStoreService.IssuerMatches(cert, "My Internal CA").Should().BeFalse();
    }

    [Fact]
    public void IssuerMatches_CaNameExtended_IsRejected()
    {
        // Same suffix hazard as the subject, on the issuer side.
        using var cert = Cert($"CN={Wanted}", issuerDn: "CN=My Internal CA Backup");
        CertificateStoreService.IssuerMatches(cert, "My Internal CA").Should().BeFalse();
    }
}
