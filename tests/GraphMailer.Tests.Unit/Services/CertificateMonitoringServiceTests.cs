using System.Security.Cryptography.X509Certificates;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Certificates;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GraphMailer.Tests.Unit.Services;

public sealed class CertificateMonitoringServiceTests
{
    private static IOptionsMonitor<T> Monitor<T>(T value)
    {
        var m = Substitute.For<IOptionsMonitor<T>>();
        m.CurrentValue.Returns(value);
        return m;
    }

    [Fact]
    public async Task ExecuteAsync_Disabled_DoesNotCheckCertificate()
    {
        var loader = Substitute.For<ICertificateLoader>();
        var notify = Substitute.For<IAdminNotificationService>();
        var opts = Monitor(new CertificateMonitoringOptions { Enabled = false });
        var svc = new CertificateMonitoringService(loader, notify, opts, NullLogger<CertificateMonitoringService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await svc.StartAsync(cts.Token);
        await Task.Delay(50);
        await svc.StopAsync(CancellationToken.None);

        loader.DidNotReceive().LoadCertificate();
    }

    [Fact]
    public async Task ExecuteAsync_NoCertificate_DoesNotNotify()
    {
        var loader = Substitute.For<ICertificateLoader>();
        loader.LoadCertificate().Returns((X509Certificate2?)null);

        var notify = Substitute.For<IAdminNotificationService>();
        var opts = Monitor(new CertificateMonitoringOptions { Enabled = true, WarningThresholdDays = 14, CheckIntervalHours = 24 });
        var svc = new CertificateMonitoringService(loader, notify, opts, NullLogger<CertificateMonitoringService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await svc.StartAsync(cts.Token);
        await Task.Delay(100);
        await svc.StopAsync(CancellationToken.None);

        await notify.DidNotReceive().NotifyCertificateExpiringAsync(
            Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await notify.DidNotReceive().NotifyCertificateExpiredAsync(
            Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ExpiringCertificate_NotifiesExpiringSoon()
    {
        // Create a real self-signed cert that expires in 5 days
        var cert = CreateSelfSignedCert(expiresInDays: 5);
        var loader = Substitute.For<ICertificateLoader>();
        loader.LoadCertificate().Returns(cert);

        var notify = Substitute.For<IAdminNotificationService>();
        var opts = Monitor(new CertificateMonitoringOptions { Enabled = true, WarningThresholdDays = 14, CheckIntervalHours = 24 });
        var svc = new CertificateMonitoringService(loader, notify, opts, NullLogger<CertificateMonitoringService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await svc.StartAsync(cts.Token);
        await Task.Delay(150);
        await svc.StopAsync(CancellationToken.None);

        await notify.Received().NotifyCertificateExpiringAsync(
            Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ExpiredCertificate_NotifiesExpired()
    {
        // Create a cert that expired 1 day ago
        var cert = CreateSelfSignedCert(expiresInDays: -1);
        var loader = Substitute.For<ICertificateLoader>();
        loader.LoadCertificate().Returns(cert);

        var notify = Substitute.For<IAdminNotificationService>();
        var opts = Monitor(new CertificateMonitoringOptions { Enabled = true, WarningThresholdDays = 14, CheckIntervalHours = 24 });
        var svc = new CertificateMonitoringService(loader, notify, opts, NullLogger<CertificateMonitoringService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await svc.StartAsync(cts.Token);
        await Task.Delay(150);
        await svc.StopAsync(CancellationToken.None);

        await notify.Received().NotifyCertificateExpiredAsync(
            Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates a self-signed RSA certificate valid from now, expiring in <paramref name="expiresInDays"/> days
    /// (negative = already expired).
    /// </summary>
    private static X509Certificate2 CreateSelfSignedCert(int expiresInDays)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new CertificateRequest("CN=test-cert", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.UtcNow;
        var from = expiresInDays < 0 ? now.AddDays(expiresInDays - 1) : now.AddDays(-1);
        var to = now.AddDays(expiresInDays);
        return req.CreateSelfSigned(from, to);
    }
}
