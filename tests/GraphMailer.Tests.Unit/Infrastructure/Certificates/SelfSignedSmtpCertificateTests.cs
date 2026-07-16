using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GraphMailer.Service.Infrastructure.Certificates;

namespace GraphMailer.Tests.Unit.Infrastructure.Certificates;

/// <summary>
/// Covers the in-memory certificate creation (<see cref="SelfSignedSmtpCertificate.CreateSelfSigned"/>).
/// Installation into the LocalMachine store is intentionally not unit-tested (needs elevation and
/// mutates the machine); it is exercised manually per the plan's verification steps.
/// </summary>
public sealed class SelfSignedSmtpCertificateTests
{
    [Fact]
    public void CreateSelfSigned_HasExpectedSubjectAndPrivateKey()
    {
        using var cert = SelfSignedSmtpCertificate.CreateSelfSigned();

        cert.GetNameInfo(X509NameType.SimpleName, false).Should().Be("GraphMailer SMTP");
        cert.HasPrivateKey.Should().BeTrue();
        using var rsa = cert.GetRSAPrivateKey();
        rsa!.KeySize.Should().Be(2048);
    }

    [Fact]
    public void CreateSelfSigned_HasServerAuthenticationEku()
    {
        using var cert = SelfSignedSmtpCertificate.CreateSelfSigned();

        var eku = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        eku.EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value)
            .Should().Contain("1.3.6.1.5.5.7.3.1"); // Server Authentication
    }

    [Fact]
    public void CreateSelfSigned_HasSanWithLocalhostAndMachineName()
    {
        using var cert = SelfSignedSmtpCertificate.CreateSelfSigned();

        var san = cert.Extensions["2.5.29.17"]!.Format(multiLine: false);
        san.Should().Contain("localhost");
        san.Should().Contain(Environment.MachineName);
    }
}
