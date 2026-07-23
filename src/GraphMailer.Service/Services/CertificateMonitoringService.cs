using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Certificates;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphMailer.Service.Services;

/// <summary>
/// BackgroundService that periodically checks certificate expiration in the Windows Certificate
/// Store. Two independent certificates are watched:
/// <list type="bullet">
///   <item>the <b>TLS listener</b> certificate securing the SMTP ports, and</item>
///   <item>the <b>Graph client</b> certificate used for Entra app-only authentication.</item>
/// </list>
/// The distinction matters for what can be reported: when the TLS certificate lapses, Graph still
/// works and both the warning and the expired alert are delivered. When the <i>Graph client</i>
/// certificate lapses, GraphMailer can no longer authenticate and therefore cannot email anything
/// about it — so that one is only ever useful as an advance warning, and the expired case is
/// logged at Error and surfaced in the operations report instead of being sent.
/// </summary>
internal sealed class CertificateMonitoringService : BackgroundService
{
    private readonly ICertificateLoader _loader;
    private readonly IAdminNotificationService _notify;
    private readonly IOptionsMonitor<CertificateMonitoringOptions> _options;
    private readonly IOptionsMonitor<GraphApiOptions> _graphApi;
    private readonly ILogger<CertificateMonitoringService> _logger;

    public CertificateMonitoringService(
        ICertificateLoader loader,
        IAdminNotificationService notify,
        IOptionsMonitor<CertificateMonitoringOptions> options,
        IOptionsMonitor<GraphApiOptions> graphApi,
        ILogger<CertificateMonitoringService> logger)
    {
        _loader = loader;
        _notify = notify;
        _options = options;
        _graphApi = graphApi;
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
        await CheckAllAsync(opts, stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(opts.CheckIntervalHours));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await CheckAllAsync(_options.CurrentValue, stoppingToken);
        }
        catch (OperationCanceledException) { }

        _logger.LogInformation("[CertMonitor] Stopped");
    }

    internal async Task CheckAllAsync(CertificateMonitoringOptions opts, CancellationToken ct)
    {
        await CheckCertificateAsync(opts, ct);
        await CheckGraphClientCertificateAsync(opts, ct);
    }

    /// <summary>
    /// Warns before the Graph client certificate expires, while Entra authentication still works
    /// and the email can therefore still be sent. Once it has expired the alert is log-only: no
    /// Graph token means no way to deliver it.
    /// </summary>
    private async Task CheckGraphClientCertificateAsync(CertificateMonitoringOptions opts, CancellationToken ct)
    {
        var graph = _graphApi.CurrentValue;
        if (!graph.HasClientCertificate)
        {
            _logger.LogDebug("[CertMonitor] Graph API does not use certificate auth – skipping Graph certificate check");
            return;
        }

        try
        {
            using var cert = GraphClientProvider.TryGetClientCertificate(graph);
            if (cert is null)
            {
                // Selection by SubjectName only ever matches a currently-valid certificate, so a
                // lapsed one simply disappears — the operator sees this rather than an expiry date.
                _logger.LogError(
                    "[CertMonitor] Graph client certificate not found in the certificate store — " +
                    "Graph authentication will fail. Check the configured thumbprint/subject name.");
                return;
            }

            var expiry = cert.NotAfter.ToUniversalTime();
            var daysLeft = (expiry - DateTime.UtcNow).TotalDays;

            if (daysLeft < 0)
            {
                _logger.LogError(
                    "[CertMonitor] Graph client certificate EXPIRED: {Subject} (expired {Expiry:R}). " +
                    "Mail delivery is down and no notification can be sent — renew the certificate and " +
                    "re-register it in Entra.",
                    cert.Subject, expiry);
                return;
            }

            if (daysLeft <= opts.WarningThresholdDays)
            {
                _logger.LogWarning(
                    "[CertMonitor] Graph client certificate expiring soon: {Subject} – {Days:F0} day(s) remaining",
                    cert.Subject, daysLeft);
                await _notify.NotifyGraphCertificateExpiringAsync(cert.Subject, expiry, ct);
            }
            else
            {
                _logger.LogDebug("[CertMonitor] Graph client certificate OK – {Days:F0} day(s) until expiry", daysLeft);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CertMonitor] Graph client certificate check failed");
        }
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
