using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GraphMailer.Tests.Unit.Services;

public sealed class AdminNotificationServiceTests : IDisposable
{
    private readonly IGraphApiClient _graph = Substitute.For<IGraphApiClient>();
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "adminnotify-tests-" + Guid.NewGuid().ToString("N"));

    private AdminNotificationService CreateService(AdminNotificationsOptions? opts = null)
    {
        var options = opts ?? new AdminNotificationsOptions
        {
            Enabled = true,
            SenderAddress = "admin@contoso.com",
            RecipientAddresses = ["ops@contoso.com"]
        };
        var monitor = Substitute.For<IOptionsMonitor<AdminNotificationsOptions>>();
        monitor.CurrentValue.Returns(options);

        var ndrMonitor = Substitute.For<IOptionsMonitor<NdrOptions>>();
        ndrMonitor.CurrentValue.Returns(new NdrOptions { Enabled = false });

        var queueOpts = Substitute.For<IOptionsMonitor<MailQueueOptions>>();
        queueOpts.CurrentValue.Returns(new MailQueueOptions { MailDir = _tempDir });
        var queueWriter = new MailQueueWriter(queueOpts, NullLogger<MailQueueWriter>.Instance);

        return new AdminNotificationService(_graph, queueWriter, monitor, ndrMonitor, NullLogger<AdminNotificationService>.Instance);
    }

    public void Dispose()
    {
        if (_graph is IDisposable d) d.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // -------------------------------------------------------------------------

    [Fact]
    public async Task NotifyCertificateExpiring_Disabled_DoesNotSend()
    {
        var svc = CreateService(new AdminNotificationsOptions { Enabled = false });
        await svc.NotifyCertificateExpiringAsync("CN=test", DateTime.UtcNow.AddDays(5));
        await _graph.DidNotReceive().SendNotificationAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task NotifyCertificateExpiring_Enabled_Sends()
    {
        var svc = CreateService();
        await svc.NotifyCertificateExpiringAsync("CN=test", DateTime.UtcNow.AddDays(5));
        await _graph.Received(1).SendNotificationAsync(
            "admin@contoso.com",
            Arg.Any<IEnumerable<string>>(),
            Arg.Is<string>(s => s.Contains("expiring")),
            Arg.Any<string>());
    }

    [Fact]
    public async Task NotifyCertificateExpired_Enabled_Sends()
    {
        var svc = CreateService();
        await svc.NotifyCertificateExpiredAsync("CN=expired", DateTime.UtcNow.AddDays(-1));
        await _graph.Received(1).SendNotificationAsync(
            "admin@contoso.com",
            Arg.Any<IEnumerable<string>>(),
            Arg.Is<string>(s => s.Contains("EXPIRED")),
            Arg.Any<string>());
    }

    [Fact]
    public async Task NotifyLowDiskSpace_Enabled_Sends()
    {
        var svc = CreateService();
        await svc.NotifyLowDiskSpaceAsync("C:\\", 4.2);
        await _graph.Received(1).SendNotificationAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(),
            Arg.Is<string>(s => s.Contains("disk")),
            Arg.Any<string>());
    }

    [Fact]
    public async Task NotifyGraphApiError_Enabled_Sends()
    {
        var svc = CreateService();
        await svc.NotifyGraphApiErrorAsync("Connection refused");
        await _graph.Received(1).SendNotificationAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(),
            Arg.Is<string>(s => s.Contains("Graph API")),
            Arg.Any<string>());
    }

    [Fact]
    public async Task NotifyBackupResult_Success_Sends_WithSucceededSubject()
    {
        var svc = CreateService();
        await svc.NotifyBackupResultAsync(succeeded: true, "File: backup.gmbak (123 bytes)");
        await _graph.Received(1).SendNotificationAsync(
            "admin@contoso.com", Arg.Any<IEnumerable<string>>(),
            Arg.Is<string>(s => s.Contains("backup succeeded")),
            Arg.Is<string>(b => b.Contains("backup.gmbak")));
    }

    [Fact]
    public async Task NotifyBackupResult_Failure_Sends_WithFailedSubject()
    {
        var svc = CreateService();
        await svc.NotifyBackupResultAsync(succeeded: false, "Backup failed: disk full");
        await _graph.Received(1).SendNotificationAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(),
            Arg.Is<string>(s => s.Contains("FAILED")),
            Arg.Is<string>(b => b.Contains("disk full")));
    }

    [Fact]
    public async Task NotifyBackupResult_TypeDisabled_DoesNotSend()
    {
        var svc = CreateService(new AdminNotificationsOptions
        {
            Enabled = true,
            SenderAddress = "admin@contoso.com",
            RecipientAddresses = ["ops@contoso.com"],
            NotificationTypes = new AdminNotificationTypesOptions { BackupResult = new() { Enabled = false } },
        });
        await svc.NotifyBackupResultAsync(succeeded: true, "x");
        await _graph.DidNotReceive().SendNotificationAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task NotifyGraphApiRestored_Enabled_Sends()
    {
        var svc = CreateService();
        await svc.NotifyGraphApiRestoredAsync();
        await _graph.Received(1).SendNotificationAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(),
            Arg.Is<string>(s => s.Contains("restored")),
            Arg.Any<string>());
    }

    [Fact]
    public async Task NotifyPortOutage_Enabled_Sends()
    {
        var svc = CreateService();
        await svc.NotifyPortOutageAsync(2525, "Unreachable");
        await _graph.Received(1).SendNotificationAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(),
            Arg.Is<string>(s => s.Contains("2525")),
            Arg.Any<string>());
    }

    [Fact]
    public async Task NotifyPortRestored_Enabled_Sends()
    {
        var svc = CreateService();
        await svc.NotifyPortRestoredAsync(2525);
        await _graph.Received(1).SendNotificationAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(),
            Arg.Is<string>(s => s.Contains("2525") && s.Contains("restored")),
            Arg.Any<string>());
    }

    [Fact]
    public async Task NotifyCertificateExpiring_NoSenderAddress_DoesNotSend()
    {
        var svc = CreateService(new AdminNotificationsOptions
        {
            Enabled = true,
            SenderAddress = null,          // not configured
            RecipientAddresses = ["ops@contoso.com"]
        });
        await svc.NotifyCertificateExpiringAsync("CN=test", DateTime.UtcNow.AddDays(5));
        await _graph.DidNotReceive().SendNotificationAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task NotifyIpBlocked_BelowThreshold_DoesNotSend()
    {
        var opts = new AdminNotificationsOptions
        {
            Enabled = true,
            SenderAddress = "admin@contoso.com",
            RecipientAddresses = ["ops@contoso.com"],
            NotificationTypes = new()
            {
                IpBlockedAlert = new() { Enabled = true, FailureThreshold = 5, TimeWindowSeconds = 300 }
            }
        };
        var svc = CreateService(opts);

        // 4 calls – below threshold of 5
        for (var i = 0; i < 4; i++)
            await svc.NotifyIpBlockedAsync("10.0.0.1");

        await _graph.DidNotReceive().SendNotificationAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task NotifyEmailDeliveryFailed_Disabled_DoesNotQueue()
    {
        var opts = new AdminNotificationsOptions
        {
            Enabled = true,
            SenderAddress = "admin@contoso.com",
            RecipientAddresses = ["ops@contoso.com"],
            NotificationTypes = new()
            {
                EmailDeliveryFailed = new() { Enabled = false, BatchDelaySeconds = 300 }
            }
        };
        var svc = CreateService(opts);
        await svc.NotifyEmailDeliveryFailedAsync("msg-1", "timeout");

        // Graph API should NOT have been called
        await _graph.DidNotReceive().SendNotificationAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<string>(), Arg.Any<string>());
    }
}
