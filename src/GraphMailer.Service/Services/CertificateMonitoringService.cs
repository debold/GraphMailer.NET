using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Certificates;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphMailer.Service.Services;

/// <summary>
/// BackgroundService that periodically checks TLS certificate expiration in the Windows
/// Certificate Store. Sends admin notifications when a certificate is expiring or has expired.
/// </summary>
internal sealed class CertificateMonitoringService : BackgroundService
{
    private readonly ICertificateLoader _loader;
    private readonly IAdminNotificationService _notify;
    private readonly IOptionsMonitor<CertificateMonitoringOptions> _options;
    private readonly ILogger<CertificateMonitoringService> _logger;

    public CertificateMonitoringService(
        ICertificateLoader loader,
        IAdminNotificationService notify,
        IOptionsMonitor<CertificateMonitoringOptions> options,
        ILogger<CertificateMonitoringService> logger)
    {
        _loader = loader;
        _notify = notify;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled)
        {
            _logger.LogDebug("[CertMonitor] Certificate monitoring disabled");
            return;
        }

        _logger.LogInformation("[CertMonitor] Started (check interval: {Hours}h, warning threshold: {Days}d)",
            opts.CheckIntervalHours, opts.WarningThresholdDays);

        // Check immediately on startup
        await CheckCertificateAsync(opts, stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(opts.CheckIntervalHours));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await CheckCertificateAsync(_options.CurrentValue, stoppingToken);
        }
        catch (OperationCanceledException) { }

        _logger.LogInformation("[CertMonitor] Stopped");
    }

    private async Task CheckCertificateAsync(CertificateMonitoringOptions opts, CancellationToken ct)
    {
        try
        {
            var cert = _loader.LoadCertificate();
            if (cert is null)
            {
                _logger.LogDebug("[CertMonitor] No certificate configured – skipping check");
                return;
            }

            var expiry = cert.NotAfter.ToUniversalTime();
            var daysLeft = (expiry - DateTime.UtcNow).TotalDays;
            var certSubject = cert.Subject;

            _logger.LogDebug("[CertMonitor] Certificate {Subject} expires in {Days:F1} day(s)", certSubject, daysLeft);

            if (daysLeft < 0)
            {
                _logger.LogError("[CertMonitor] Certificate EXPIRED: {Subject} (expired {Expiry:R})", certSubject, expiry);
                await _notify.NotifyCertificateExpiredAsync(certSubject, expiry, ct);
            }
            else if (daysLeft <= opts.WarningThresholdDays)
            {
                _logger.LogWarning("[CertMonitor] Certificate expiring soon: {Subject} – {Days:F0} day(s) remaining",
                    certSubject, daysLeft);
                await _notify.NotifyCertificateExpiringAsync(certSubject, expiry, ct);
            }
            else
            {
                _logger.LogDebug("[CertMonitor] Certificate OK – {Days:F0} day(s) until expiry", daysLeft);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CertMonitor] Certificate check failed");
        }
    }
}
