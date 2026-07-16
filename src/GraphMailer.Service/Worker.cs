using GraphMailer.Service.Services;

namespace GraphMailer.Service;

internal sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IAdminNotificationService _notify;

    public Worker(ILogger<Worker> logger, IAdminNotificationService notify)
    {
        _logger = logger;
        _notify = notify;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[GraphMailer] Service started at {Time}", DateTimeOffset.UtcNow);
        await _notify.NotifyServiceStartedAsync(stoppingToken);

        try
        {
            // Phase 1 placeholder – subsequent phases will register dedicated IHostedServices
            // (SmtpRelayService, MailQueueProcessor, MetricsService, etc.)
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on graceful shutdown – not an error
        }
        finally
        {
            _logger.LogInformation("[GraphMailer] Service stopped at {Time}", DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Sends the stop notification BEFORE signalling ExecuteAsync to stop.
    /// base.StopAsync cancels the stoppingToken, so the notification would race
    /// with shutdown if sent inside ExecuteAsync's finally block.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // cancellationToken here is the shutdown-timeout token (typically 30s),
        // NOT the stoppingToken used inside ExecuteAsync.
        await _notify.NotifyServiceStoppedAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }
}
