using FluentAssertions;
using GraphMailer.Service.Infrastructure.Config;
using Microsoft.AspNetCore.DataProtection;

namespace GraphMailer.Tests.Unit.Infrastructure.Config;

/// <summary>
/// Contract tests for the Load direction: JSON written with service-side keys
/// (i.e. the exact keys read by Options classes at runtime) must be read back
/// into ConfigDocument by ConfigService.Load() so the ConfigTool can display
/// and edit the values.
///
/// Pattern for each test:
///   1. Write a JSON file that uses the real Options JSON key (SectionName:Property).
///   2. Call ConfigService.Load() – the same code path used at startup.
///   3. Assert the matching ConfigDocument property holds the expected value.
///
/// A failing test means ConfigService.Read*() ignores that JSON key, so a value
/// set by a deployment engineer in graphmailer.json would be silently discarded
/// the next time the ConfigTool saves, reverting the service to its default.
///
/// When to add a test here:
///   • A new property is added to any *Options class in Configuration/.
///   • A new section is bound in Program.cs.
///   • A ConfigDocument property is renamed or removed.
/// </summary>
public sealed class ConfigSchemaLoadTests : IDisposable
{
    private readonly string _dir;
    private readonly string _filePath;
    private readonly ConfigService _sut;

    public ConfigSchemaLoadTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"gm-load-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _filePath = Path.Combine(_dir, "graphmailer.json");
        var protector = new EphemeralDataProtectionProvider().CreateProtector("GraphMailer.Config");
        _sut = new ConfigService(_filePath, protector);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private void WriteJson(string json)
        => File.WriteAllText(_filePath, json);

    // =========================================================================
    // Certificate  (SectionName = "Certificate")
    // Maps to ConfigDocument.CertSection
    // =========================================================================

    [Fact]
    public void Load_Certificate_FailClosed_AppearsInDocCertificateFailClosed()
    {
        WriteJson("""{ "Certificate": { "FailClosed": true } }""");

        _sut.Load().Certificate.FailClosed.Should().BeTrue();
    }

    [Fact]
    public void Load_Certificate_FailClosedAbsent_DefaultsToFalse()
    {
        WriteJson("""{ "Certificate": { "SubjectName": "smtp.local" } }""");

        _sut.Load().Certificate.FailClosed.Should().BeFalse();
    }

    // =========================================================================
    // CertificateMonitoring  (SectionName = "CertificateMonitoring")
    // Maps to ConfigDocument.MonitoringSection
    // =========================================================================

    [Fact]
    public void Load_CertificateMonitoring_WarningThresholdDays_AppearsInDocMonitoringCertWarnDays()
    {
        WriteJson("""{ "CertificateMonitoring": { "WarningThresholdDays": 7 } }""");

        var doc = _sut.Load();

        doc.Monitoring.CertWarnDays.Should().Be(7);
    }

    // =========================================================================
    // DiskSpaceMonitoring  (SectionName = "DiskSpaceMonitoring")
    // =========================================================================

    [Fact]
    public void Load_DiskSpaceMonitoring_ThresholdPercent_AppearsInDocMonitoringDiskWarnPct()
    {
        WriteJson("""{ "DiskSpaceMonitoring": { "ThresholdPercent": 25 } }""");

        var doc = _sut.Load();

        doc.Monitoring.DiskWarnPct.Should().Be(25);
    }

    // =========================================================================
    // PortMonitoring  (SectionName = "PortMonitoring")
    // =========================================================================

    [Fact]
    public void Load_PortMonitoring_CheckIntervalMinutes_AppearsInDocMonitoringPortCheckInterval()
    {
        WriteJson("""{ "PortMonitoring": { "CheckIntervalMinutes": 3 } }""");

        var doc = _sut.Load();

        doc.Monitoring.PortCheckIntervalMinutes.Should().Be(3);
    }

    // =========================================================================
    // GraphApiMonitoring  (SectionName = "GraphApiMonitoring")
    // =========================================================================

    [Fact]
    public void Load_GraphApiMonitoring_CheckIntervalMinutes_AppearsInDocMonitoringGraphCheckInterval()
    {
        WriteJson("""{ "GraphApiMonitoring": { "CheckIntervalMinutes": 30 } }""");

        var doc = _sut.Load();

        doc.Monitoring.GraphCheckIntervalMinutes.Should().Be(30);
    }

    // =========================================================================
    // UpdateCheck  (SectionName = "UpdateCheck")
    // =========================================================================

    [Fact]
    public void Load_UpdateCheck_Enabled_AppearsInDocMonitoringUpdateCheckEnabled()
    {
        WriteJson("""{ "UpdateCheck": { "Enabled": true } }""");

        _sut.Load().Monitoring.UpdateCheckEnabled.Should().BeTrue();
    }

    [Fact]
    public void Load_UpdateCheck_Absent_DefaultsToDisabled()
    {
        WriteJson("""{ "Smtp": { "Banner": "test" } }""");

        _sut.Load().Monitoring.UpdateCheckEnabled.Should().BeFalse();
    }

    // =========================================================================
    // AdminNotifications  (SectionName = "AdminNotifications")
    // Maps to ConfigDocument.NotificationSection
    // =========================================================================

    [Fact]
    public void Load_AdminNotifications_RecipientAddresses_AppearsInDocNotificationRecipientAddresses()
    {
        WriteJson("""
        {
            "AdminNotifications": {
                "RecipientAddresses": ["ops@corp.com"]
            }
        }
        """);

        var doc = _sut.Load();

        doc.Notification.RecipientAddresses.Should().ContainSingle().Which.Should().Be("ops@corp.com");
    }

    [Fact]
    public void Load_AdminNotifications_SenderAddress_AppearsInDocNotificationNotifFrom()
    {
        WriteJson("""
        {
            "AdminNotifications": {
                "SenderAddress": "relay@corp.com"
            }
        }
        """);

        var doc = _sut.Load();

        doc.Notification.NotifFrom.Should().Be("relay@corp.com");
    }

    [Fact]
    public void Load_AdminNotifications_SubjectPrefix_AppearsInDocNotificationSubjectPrefix()
    {
        WriteJson("""
        {
            "AdminNotifications": {
                "SubjectPrefix": "[PROD]"
            }
        }
        """);

        var doc = _sut.Load();

        doc.Notification.SubjectPrefix.Should().Be("[PROD]");
    }

    // ── NotificationTypes toggles ─────────────────────────────────────────────

    [Fact]
    public void Load_AdminNotifications_IpBlockedAlert_Disabled_AppearsInDocNotifIpBlocked_False()
    {
        WriteJson("""
        {
            "AdminNotifications": {
                "NotificationTypes": { "IpBlockedAlert": { "Enabled": false } }
            }
        }
        """);

        _sut.Load().Notification.NotifIpBlocked.Should().BeFalse();
    }

    [Fact]
    public void Load_AdminNotifications_EmailDeliveryFailed_Disabled_AppearsInDocNotifDeliveryFailed_False()
    {
        WriteJson("""
        {
            "AdminNotifications": {
                "NotificationTypes": { "EmailDeliveryFailed": { "Enabled": false } }
            }
        }
        """);

        _sut.Load().Notification.NotifDeliveryFailed.Should().BeFalse();
    }

    [Fact]
    public void Load_AdminNotifications_CertificateExpiringWarning_Disabled_AppearsInDocNotifCertExpiring_False()
    {
        WriteJson("""
        {
            "AdminNotifications": {
                "NotificationTypes": { "CertificateExpiringWarning": { "Enabled": false } }
            }
        }
        """);

        _sut.Load().Notification.NotifCertExpiring.Should().BeFalse();
    }

    [Fact]
    public void Load_AdminNotifications_CertificateExpired_Disabled_AppearsInDocNotifCertExpired_False()
    {
        WriteJson("""
        {
            "AdminNotifications": {
                "NotificationTypes": { "CertificateExpired": { "Enabled": false } }
            }
        }
        """);

        _sut.Load().Notification.NotifCertExpired.Should().BeFalse();
    }

    [Fact]
    public void Load_AdminNotifications_LowDiskSpaceWarning_Disabled_AppearsInDocNotifDiskSpace_False()
    {
        WriteJson("""
        {
            "AdminNotifications": {
                "NotificationTypes": { "LowDiskSpaceWarning": { "Enabled": false } }
            }
        }
        """);

        _sut.Load().Notification.NotifDiskSpace.Should().BeFalse();
    }

    [Fact]
    public void Load_AdminNotifications_GraphApiConnectionError_Disabled_AppearsInDocNotifGraphDown_False()
    {
        WriteJson("""
        {
            "AdminNotifications": {
                "NotificationTypes": { "GraphApiConnectionError": { "Enabled": false } }
            }
        }
        """);

        _sut.Load().Notification.NotifGraphDown.Should().BeFalse();
    }

    [Fact]
    public void Load_AdminNotifications_PortMonitoringAlert_Disabled_AppearsInDocNotifPortDown_False()
    {
        WriteJson("""
        {
            "AdminNotifications": {
                "NotificationTypes": { "PortMonitoringAlert": { "Enabled": false } }
            }
        }
        """);

        _sut.Load().Notification.NotifPortDown.Should().BeFalse();
    }

    // =========================================================================
    // Servers – AuthMode backward compatibility
    // =========================================================================

    [Fact]
    public void Load_Server_AuthRequired_True_MapsToAuthMode_Required()
    {
        WriteJson("""
        {
            "Servers": [{ "Port": 587, "Mode": "StartTls", "AuthRequired": true }]
        }
        """);

        _sut.Load().Servers[0].AuthMode.Should().Be("Required");
    }

    [Fact]
    public void Load_Server_AuthMode_None_RoundTripsCorrectly()
    {
        WriteJson("""
        {
            "Servers": [{ "Port": 2525, "Mode": "Plain", "AuthMode": "None" }]
        }
        """);

        _sut.Load().Servers[0].AuthMode.Should().Be("None");
    }

    // =========================================================================
    // Meta: all mappings together – catches schema drift across sections
    // =========================================================================

    [Fact]
    public void Load_AllMonitoringAndNotificationKeys_AllMappedToConfigDocument()
    {
        WriteJson("""
        {
            "CertificateMonitoring":  { "WarningThresholdDays": 7  },
            "DiskSpaceMonitoring":    { "ThresholdPercent":     25 },
            "PortMonitoring":         { "CheckIntervalMinutes": 3  },
            "GraphApiMonitoring":     { "CheckIntervalMinutes": 30 },
            "AdminNotifications": {
                "SenderAddress":     "relay@corp.com",
                "RecipientAddresses": ["ops@corp.com"],
                "SubjectPrefix":     "[META]",
                "NotificationTypes": {
                    "IpBlockedAlert":            { "Enabled": false },
                    "EmailDeliveryFailed":        { "Enabled": false },
                    "CertificateExpiringWarning": { "Enabled": false },
                    "CertificateExpired":         { "Enabled": false },
                    "LowDiskSpaceWarning":        { "Enabled": false },
                    "GraphApiConnectionError":    { "Enabled": false },
                    "PortMonitoringAlert":        { "Enabled": false }
                }
            }
        }
        """);

        var doc = _sut.Load();

        doc.Monitoring.CertWarnDays.Should().Be(7);
        doc.Monitoring.DiskWarnPct.Should().Be(25);
        doc.Monitoring.PortCheckIntervalMinutes.Should().Be(3);
        doc.Monitoring.GraphCheckIntervalMinutes.Should().Be(30);

        doc.Notification.RecipientAddresses.Should().ContainSingle().Which.Should().Be("ops@corp.com");
        doc.Notification.NotifFrom.Should().Be("relay@corp.com");
        doc.Notification.SubjectPrefix.Should().Be("[META]");
        doc.Notification.NotifIpBlocked.Should().BeFalse();
        doc.Notification.NotifDeliveryFailed.Should().BeFalse();
        doc.Notification.NotifCertExpiring.Should().BeFalse();
        doc.Notification.NotifCertExpired.Should().BeFalse();
        doc.Notification.NotifDiskSpace.Should().BeFalse();
        doc.Notification.NotifGraphDown.Should().BeFalse();
        doc.Notification.NotifPortDown.Should().BeFalse();
    }

    // =========================================================================
    // MailQueue  (SectionName = "MailQueue")
    // =========================================================================

    [Fact]
    public void Load_MailQueue_MailDir_AppearsInDocMailQueueMailDir()
    {
        WriteJson("""{ "MailQueue": { "MailDir": "D:\\MailStorage" } }""");

        var doc = _sut.Load();

        doc.MailQueue.MailDir.Should().Be("D:\\MailStorage");
    }

    [Fact]
    public void Load_MailQueue_FailedEmailRetentionDays_AppearsInDocMailQueue()
    {
        WriteJson("""{ "MailQueue": { "FailedEmailRetentionDays": 14 } }""");

        _sut.Load().MailQueue.FailedEmailRetentionDays.Should().Be(14);
    }

    [Fact]
    public void Load_MailQueue_RetryPolicy_AppearsInDocMailQueue()
    {
        WriteJson("""
        {
            "MailQueue": {
                "TransientRetryCount": 4,
                "TransientRetryIntervalSeconds": 120,
                "RetryIntervalSeconds": 600,
                "MessageExpirationHours": 48
            }
        }
        """);

        var q = _sut.Load().MailQueue;

        q.TransientRetryCount.Should().Be(4);
        q.TransientRetryIntervalSeconds.Should().Be(120);
        q.RetryIntervalSeconds.Should().Be(600);
        q.MessageExpirationHours.Should().Be(48);
    }

    // =========================================================================
    // Metrics  (SectionName = "Metrics")
    // Maps to ConfigDocument.MetricsSection
    // =========================================================================

    [Fact]
    public void Load_Metrics_Enabled_False_AppearsInDocMetricsEnabled_False()
    {
        WriteJson("""{ "Metrics": { "Enabled": false } }""");

        _sut.Load().Metrics.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Load_Metrics_RetentionDays_AppearsInDocMetricsRetentionDays()
    {
        WriteJson("""{ "Metrics": { "RetentionDays": 30 } }""");

        _sut.Load().Metrics.RetentionDays.Should().Be(30);
    }

    [Fact]
    public void Load_Metrics_CleanupIntervalHours_AppearsInDocMetricsCleanupIntervalHours()
    {
        WriteJson("""{ "Metrics": { "CleanupIntervalHours": 12 } }""");

        _sut.Load().Metrics.CleanupIntervalHours.Should().Be(12);
    }

    [Fact]
    public void Load_Metrics_PerformanceMetrics_Enabled_False_AppearsInDocMetricsPerfMetricsEnabled_False()
    {
        WriteJson("""{ "Metrics": { "PerformanceMetrics": { "Enabled": false } } }""");

        _sut.Load().Metrics.PerfMetricsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Load_Metrics_PerformanceMetrics_MemoryIntervalSeconds_AppearsInDocMetricsPerfMemoryIntervalSeconds()
    {
        WriteJson("""{ "Metrics": { "PerformanceMetrics": { "MemoryIntervalSeconds": 120 } } }""");

        _sut.Load().Metrics.PerfMemoryIntervalSeconds.Should().Be(120);
    }

    [Fact]
    public void Load_Metrics_PerformanceMetrics_CpuIntervalSeconds_AppearsInDocMetricsPerfCpuIntervalSeconds()
    {
        WriteJson("""{ "Metrics": { "PerformanceMetrics": { "CpuIntervalSeconds": 30 } } }""");

        _sut.Load().Metrics.PerfCpuIntervalSeconds.Should().Be(30);
    }

    [Fact]
    public void Load_Metrics_PerformanceMetrics_DiskIntervalSeconds_AppearsInDocMetricsPerfDiskIntervalSeconds()
    {
        WriteJson("""{ "Metrics": { "PerformanceMetrics": { "DiskIntervalSeconds": 600 } } }""");

        _sut.Load().Metrics.PerfDiskIntervalSeconds.Should().Be(600);
    }

    [Fact]
    public void Load_AdminNotifications_BackupResult_Disabled_AppearsInDocNotifBackup_False()
    {
        WriteJson("""{ "AdminNotifications": { "NotificationTypes": { "BackupResult": { "Enabled": false } } } }""");

        _sut.Load().Notification.NotifBackup.Should().BeFalse();
    }

    [Fact]
    public void Load_AdminNotifications_UpdateAvailable_Enabled_AppearsInDocNotifUpdateAvailable_True()
    {
        WriteJson("""{ "AdminNotifications": { "NotificationTypes": { "UpdateAvailable": { "Enabled": true } } } }""");

        _sut.Load().Notification.NotifUpdateAvailable.Should().BeTrue();
    }

    [Fact]
    public void Load_AdminNotifications_UpdateAvailable_Absent_DefaultsToDisabled()
    {
        WriteJson("""{ "AdminNotifications": { "NotificationTypes": { } } }""");

        _sut.Load().Notification.NotifUpdateAvailable.Should().BeFalse();
    }

    [Fact]
    public void Load_AdminNotifications_ScheduledReport_AllFields_AppearInDocNotification()
    {
        WriteJson("""
        {
            "AdminNotifications": {
                "ScheduledReport": {
                    "Enabled": true,
                    "Frequency": "Monthly",
                    "TimeOfDay": "08:30",
                    "DayOfWeek": "Friday",
                    "DayOfMonth": 5
                }
            }
        }
        """);

        var n = _sut.Load().Notification;

        n.ReportEnabled.Should().BeTrue();
        n.ReportFrequency.Should().Be("Monthly");
        n.ReportTimeOfDay.Should().Be("08:30");
        n.ReportDayOfWeek.Should().Be("Friday");
        n.ReportDayOfMonth.Should().Be(5);
    }

    // =========================================================================
    // Backup  (SectionName = "Backup")  →  ConfigDocument.BackupSection
    // =========================================================================

    [Fact]
    public void Load_Backup_AllFields_AppearInDocBackup()
    {
        WriteJson("""
        {
            "Backup": {
                "Enabled": true,
                "Frequency": "Weekly",
                "TimeOfDay": "04:15",
                "DayOfWeek": "Wednesday",
                "MaxBackups": 7,
                "Directory": "D:\\backups",
                "Email": { "Enabled": true, "Recipients": [ "ops@corp.com" ] }
            }
        }
        """);

        var b = _sut.Load().Backup;

        b.BackupEnabled.Should().BeTrue();
        b.Frequency.Should().Be("Weekly");
        b.TimeOfDay.Should().Be("04:15");
        b.DayOfWeek.Should().Be("Wednesday");
        b.MaxBackups.Should().Be(7);
        b.Directory.Should().Be(@"D:\backups");
        b.EmailEnabled.Should().BeTrue();
        b.EmailRecipients.Should().ContainSingle().Which.Should().Be("ops@corp.com");
    }
}
