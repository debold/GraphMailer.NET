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

    /// <summary>Graph configured with a client secret — the Graph certificate check is skipped.</summary>
    private static IOptionsMonitor<GraphApiOptions> NoGraphCert
        => Monitor(new GraphApiOptions { TenantId = "t", ClientId = "c", ClientSecret = "s" });

    // -------------------------------------------------------------------------
    // Hosted-service lifecycle: that ExecuteAsync runs a check at all, and honours
    // the Enabled switch. What a check *concludes* is covered by the CheckAll tests
    // below, which call the check directly and involve no timing whatsoever.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_Enabled_RunsTheCheckOnStartup()
    {
        using var cert = CreateSelfSignedCert(expiresInDays: 200);
        var loader = Substitute.For<ICertificateLoader>();
        loader.LoadCertificate().Returns(cert);

        // Waiting for the call itself rather than for a fixed span: a hard-coded delay turns a
        // loaded build agent into a test failure, which is exactly what this test used to do.
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notify = Substitute.For<IAdminNotificationService>();
        notify.NotifyCertificateRenewedAsync(Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(_ => { called.TrySetResult(); return Task.CompletedTask; });

        var svc = new CertificateMonitoringService(
            loader, notify, CertOpts(), NoGraphCert, NullLogger<CertificateMonitoringService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        try
        {
            // Generous: it only ever elapses if the startup check never runs at all
            await called.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }

        loader.Received().LoadCertificate();
    }

    [Fact]
    public async Task ExecuteAsync_Disabled_DoesNotCheckCertificate()
    {
        var loader = Substitute.For<ICertificateLoader>();
        var notify = Substitute.For<IAdminNotificationService>();
        var opts = Monitor(new CertificateMonitoringOptions { Enabled = false });
        var svc = new CertificateMonitoringService(loader, notify, opts, NoGraphCert, NullLogger<CertificateMonitoringService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        // No positive signal to wait for here — the assertion is that nothing happens. A fixed
        // wait is sound in that direction: extra time can only make the check stricter, never
        // produce the false failure a positive assertion would.
        await Task.Delay(100);
        await svc.StopAsync(CancellationToken.None);

        loader.DidNotReceive().LoadCertificate();
    }

    // -------------------------------------------------------------------------
    // What a check concludes, per certificate state
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CheckAll_NoCertificate_DoesNotNotify()
    {
        var notify = Substitute.For<IAdminNotificationService>();
        var svc = new CertificateMonitoringService(
            NoCertLoader(), notify, CertOpts(), NoGraphCert, NullLogger<CertificateMonitoringService>.Instance);

        await svc.CheckAllAsync(CertOpts().CurrentValue, CancellationToken.None);

        await notify.DidNotReceive().NotifyCertificateExpiringAsync(
            Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await notify.DidNotReceive().NotifyCertificateExpiredAsync(
            Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAll_ExpiringCertificate_NotifiesExpiringSoon()
    {
        using var cert = CreateSelfSignedCert(expiresInDays: 5);
        var loader = Substitute.For<ICertificateLoader>();
        loader.LoadCertificate().Returns(cert);

        var notify = Substitute.For<IAdminNotificationService>();
        var svc = new CertificateMonitoringService(
            loader, notify, CertOpts(), NoGraphCert, NullLogger<CertificateMonitoringService>.Instance);

        await svc.CheckAllAsync(CertOpts().CurrentValue, CancellationToken.None);

        await notify.Received().NotifyCertificateExpiringAsync(
            Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAll_ExpiredCertificate_NotifiesExpired()
    {
        using var cert = CreateSelfSignedCert(expiresInDays: -1);
        var loader = Substitute.For<ICertificateLoader>();
        loader.LoadCertificate().Returns(cert);

        var notify = Substitute.For<IAdminNotificationService>();
        var svc = new CertificateMonitoringService(
            loader, notify, CertOpts(), NoGraphCert, NullLogger<CertificateMonitoringService>.Instance);

        await svc.CheckAllAsync(CertOpts().CurrentValue, CancellationToken.None);

        await notify.Received().NotifyCertificateExpiredAsync(
            Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAll_HealthyCertificate_ReportsRenewedState()
    {
        using var cert = CreateSelfSignedCert(expiresInDays: 200);
        var loader = Substitute.For<ICertificateLoader>();
        loader.LoadCertificate().Returns(cert);

        var notify = Substitute.For<IAdminNotificationService>();
        var svc = new CertificateMonitoringService(
            loader, notify, CertOpts(), NoGraphCert, NullLogger<CertificateMonitoringService>.Instance);

        await svc.CheckAllAsync(CertOpts().CurrentValue, CancellationToken.None);

        // The healthy state is reported unconditionally; the alert store decides whether that
        // becomes an all-clear mail. Without it a renewal after an expiry alert would go unnoticed.
        await notify.Received().NotifyCertificateRenewedAsync(
            Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await notify.DidNotReceive().NotifyCertificateExpiringAsync(
            Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // Graph client certificate (Entra auth) — a different certificate from the TLS one
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CheckAll_GraphUsesClientSecret_DoesNotWarnAboutTheGraphCertificate()
    {
        var notify = Substitute.For<IAdminNotificationService>();
        var svc = new CertificateMonitoringService(
            NoCertLoader(), notify, CertOpts(), NoGraphCert, NullLogger<CertificateMonitoringService>.Instance);

        await svc.CheckAllAsync(CertOpts().CurrentValue, CancellationToken.None);

        await notify.DidNotReceive().NotifyGraphCertificateExpiringAsync(
            Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAll_GraphCertificateNotInStore_DoesNotNotifyAndDoesNotThrow()
    {
        // A thumbprint that cannot resolve is an operator error worth logging, but there is no
        // expiry date to warn about — and the check must not take the whole monitor down.
        var notify = Substitute.For<IAdminNotificationService>();
        var graph = Monitor(new GraphApiOptions
        {
            TenantId = "t",
            ClientId = "c",
            ClientCertificateThumbprint = "0000000000000000000000000000000000000000",
        });
        var svc = new CertificateMonitoringService(
            NoCertLoader(), notify, CertOpts(), graph, NullLogger<CertificateMonitoringService>.Instance);

        await svc.CheckAllAsync(CertOpts().CurrentValue, CancellationToken.None);

        await notify.DidNotReceive().NotifyGraphCertificateExpiringAsync(
            Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAll_TlsCertificateExpiring_StillWarnsWhileGraphUsesACertificate()
    {
        // The two certificates are independent: a lapsing TLS certificate must still be reported
        // even on an installation that authenticates to Graph with a certificate of its own.
        using var cert = CreateSelfSignedCert(expiresInDays: 5);
        var loader = Substitute.For<ICertificateLoader>();
        loader.LoadCertificate().Returns(cert);

        var notify = Substitute.For<IAdminNotificationService>();
        var graph = Monitor(new GraphApiOptions
        {
            TenantId = "t",
            ClientId = "c",
            ClientCertificateThumbprint = "0000000000000000000000000000000000000000",
        });
        var svc = new CertificateMonitoringService(
            loader, notify, CertOpts(), graph, NullLogger<CertificateMonitoringService>.Instance);

        await svc.CheckAllAsync(CertOpts().CurrentValue, CancellationToken.None);

        await notify.Received().NotifyCertificateExpiringAsync(
            Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    private static ICertificateLoader NoCertLoader()
    {
        var loader = Substitute.For<ICertificateLoader>();
        loader.LoadCertificate().Returns((X509Certificate2?)null);
        return loader;
    }

    private static IOptionsMonitor<CertificateMonitoringOptions> CertOpts()
        => Monitor(new CertificateMonitoringOptions { Enabled = true, WarningThresholdDays = 14, CheckIntervalHours = 24 });

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
