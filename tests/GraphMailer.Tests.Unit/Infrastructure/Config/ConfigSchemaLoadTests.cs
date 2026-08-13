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

    [Fact]
    public void Load_CertificateMonitoring_EnabledAndInterval_AppearInDocMonitoring()
    {
        WriteJson("""{ "CertificateMonitoring": { "Enabled": false, "CheckIntervalHours": 6 } }""");

        var doc = _sut.Load();

        doc.Monitoring.CertMonitoringEnabled.Should().BeFalse();
        doc.Monitoring.CertCheckIntervalHours.Should().Be(6);
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

    [Fact]
    public void Load_DiskSpaceMonitoring_EnabledAndInterval_AppearInDocMonitoring()
    {
        WriteJson("""{ "DiskSpaceMonitoring": { "Enabled": false, "CheckIntervalMinutes": 15 } }""");

        var doc = _sut.Load();

        doc.Monitoring.DiskMonitoringEnabled.Should().BeFalse();
        doc.Monitoring.DiskCheckIntervalMinutes.Should().Be(15);
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

    [Fact]
    public void Load_PortMonitoring_EnabledAndOutageThreshold_AppearInDocMonitoring()
    {
        WriteJson("""{ "PortMonitoring": { "Enabled": false, "OutageAlertThresholdMinutes": 30 } }""");

        var doc = _sut.Load();

        doc.Monitoring.PortMonitoringEnabled.Should().BeFalse();
        doc.Monitoring.PortOutageThresholdMinutes.Should().Be(30);
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

    [Fact]
    public void Load_GraphApiMonitoring_Enabled_AppearsInDocMonitoring()
    {
        WriteJson("""{ "GraphApiMonitoring": { "Enabled": false } }""");

        _sut.Load().Monitoring.GraphMonitoringEnabled.Should().BeFalse();
    }

    [Fact]
    public void Load_Monitoring_AllSectionsAbsent_EveryMonitorDefaultsToEnabled()
    {
        WriteJson("""{ "Smtp": { "Banner": "test" } }""");

        var m = _sut.Load().Monitoring;

        m.CertMonitoringEnabled.Should().BeTrue();
        m.DiskMonitoringEnabled.Should().BeTrue();
        m.PortMonitoringEnabled.Should().BeTrue();
        m.GraphMonitoringEnabled.Should().BeTrue();
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
    // Telemetry  (SectionName = "Telemetry")
    // =========================================================================

    [Fact]
    public void Load_Telemetry_Enabled_AppearsInDocMonitoringTelemetryEnabled()
    {
        WriteJson("""{ "Telemetry": { "Enabled": true } }""");

        _sut.Load().Monitoring.TelemetryEnabled.Should().BeTrue();
    }

    [Fact]
    public void Load_Telemetry_Absent_DefaultsToDisabled()
    {
        WriteJson("""{ "Smtp": { "Banner": "test" } }""");

        _sut.Load().Monitoring.TelemetryEnabled.Should().BeFalse("telemetry is strictly opt-in");
    }

    // =========================================================================
    // AdminNotifications  (SectionName = "AdminNotifications")
    // Maps to ConfigDocument.NotificationSection
    // =========================================================================

    [Fact]
    public void Load_AdminNotifications_RepeatAndRecovery_AppearInDocNotification()
    {
        WriteJson("""{ "AdminNotifications": { "RenotifyMinutes": 60, "SendRecoveryNotification": false } }""");

        var doc = _sut.Load();

        doc.Notification.RenotifyMinutes.Should().Be(60);
        doc.Notification.SendRecoveryNotification.Should().BeFalse();
    }

    [Fact]
    public void Load_AdminNotifications_RepeatAndRecoveryAbsent_DefaultToDailyRepeatWithAllClear()
    {
        WriteJson("""{ "AdminNotifications": { "SubjectPrefix": "[GM]" } }""");

        var doc = _sut.Load();

        doc.Notification.RenotifyMinutes.Should().Be(1440);
        doc.Notification.SendRecoveryNotification.Should().BeTrue();
    }

    [Fact]
    public void Load_AdminNotifications_RenotifyZero_IsPreservedAsReportOnce()
    {
        // 0 is a meaningful setting ("tell me once"), not a missing value — it must not be
        // mistaken for "unset" and replaced by the default.
        WriteJson("""{ "AdminNotifications": { "RenotifyMinutes": 0 } }""");

        _sut.Load().Notification.RenotifyMinutes.Should().Be(0);
    }

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

    // =========================================================================
    // Recommendations  (SectionName = "Recommendations")
    // →  ConfigDocument.RecommendationsSection
    // =========================================================================

    [Fact]
    public void Load_AdminNotifications_GraphCertificateExpiringWarning_Disabled_AppearsInDocNotifGraphCertExpiring_False()
    {
        WriteJson("""{ "AdminNotifications": { "NotificationTypes": { "GraphCertificateExpiringWarning": { "Enabled": false } } } }""");

        _sut.Load().Notification.NotifGraphCertExpiring.Should().BeFalse();
    }

    [Fact]
    public void Load_AdminNotifications_GraphCertificateExpiringWarning_Absent_DefaultsToEnabled()
    {
        // Pre-v7 config: the warning is the last one an operator gets before Graph auth dies, so
        // it must default to on rather than silently staying off after an upgrade.
        WriteJson("""{ "AdminNotifications": { "NotificationTypes": { } } }""");

        _sut.Load().Notification.NotifGraphCertExpiring.Should().BeTrue();
    }

    [Fact]
    public void Load_AdminNotifications_Enabled_AppearsInDocNotificationNotifEnabled()
    {
        WriteJson("""{ "AdminNotifications": { "Enabled": false, "RecipientAddresses": [ "ops@corp.com" ] } }""");

        _sut.Load().Notification.NotifEnabled.Should().BeFalse(
            "since schema v6 the flag is authoritative, not derived from the recipient count");
    }

    [Fact]
    public void Load_AdminNotifications_EnabledAbsentWithRecipients_FallsBackToTheDerivedValue()
    {
        // Pre-v6 files (and restored backups that bypass the migration) must keep working.
        WriteJson("""{ "AdminNotifications": { "RecipientAddresses": [ "ops@corp.com" ] } }""");

        _sut.Load().Notification.NotifEnabled.Should().BeTrue();
    }

    [Fact]
    public void Load_AdminNotifications_EnabledAbsentWithoutRecipients_IsDisabled()
    {
        WriteJson("""{ "AdminNotifications": { "RecipientAddresses": [] } }""");

        _sut.Load().Notification.NotifEnabled.Should().BeFalse();
    }

    [Fact]
    public void Load_Server_AuthMode_AppearsInDocServerAuthMode()
    {
        WriteJson("""{ "Servers": [ { "Name": "SMTP", "Port": 25, "Mode": "Plain", "AuthMode": "None" } ] }""");

        _sut.Load().Servers.Should().ContainSingle().Which.AuthMode.Should().Be("None");
    }

    [Fact]
    public void Load_Recommendations_Dismissed_AppearsInDocRecommendationsDismissed()
    {
        WriteJson("""{ "Recommendations": { "Dismissed": [ "telemetry", "log-level" ] } }""");

        _sut.Load().Recommendations.Dismissed.Should().Equal("telemetry", "log-level");
    }

    [Fact]
    public void Load_Recommendations_Absent_DefaultsToNothingDismissed()
    {
        WriteJson("""{ "Smtp": { "Banner": "test" } }""");

        _sut.Load().Recommendations.Dismissed.Should().BeEmpty("every applicable hint is shown until hidden");
    }

    // =========================================================================
    // Smtp  (SectionName = "Smtp")
    // =========================================================================

    [Fact]
    public void Load_Smtp_MaxRecipients_AppearsInDocSmtpMaxRecipients()
    {
        WriteJson("""{ "Smtp": { "MaxRecipients": 800 } }""");

        _sut.Load().Smtp.MaxRecipients.Should().Be(800);
    }

    [Fact]
    public void Load_Smtp_MaxRecipientsAbsent_DefaultsTo500()
    {
        WriteJson("""{ "Smtp": { "Banner": "test" } }""");

        _sut.Load().Smtp.MaxRecipients.Should().Be(500,
            "500 is Exchange Online's own default for a mailbox's RecipientLimits");
    }

    // =========================================================================
    // MalwareScan  (SectionName = "MalwareScan")
    // Maps to ConfigDocument.MalwareScanSection
    // =========================================================================

    [Fact]
    public void Load_MalwareScan_Scalars_AppearInDoc()
    {
        WriteJson("""
            {
              "MalwareScan": {
                "Mode": "Enforce",
                "TimeoutSeconds": 45,
                "MaxScanBytes": 1048576,
                "BlockedRecordRetentionDays": 90
              }
            }
            """);

        var doc = _sut.Load().MalwareScan;

        doc.Mode.Should().Be("Enforce");
        doc.TimeoutSeconds.Should().Be(45);
        doc.MaxScanBytes.Should().Be(1_048_576);
        doc.BlockedRecordRetentionDays.Should().Be(90);
    }

    [Fact]
    public void Load_MalwareScan_Absent_DefaultsToAuditMode()
    {
        // The upgrade contract: a config written before this feature existed must not start
        // rejecting mail. Audit observes and reports, Enforce blocks — absent means observe.
        WriteJson("""{ "Smtp": { "Banner": "test" } }""");

        _sut.Load().MalwareScan.Mode.Should().Be("Audit");
    }

    [Fact]
    public void Load_MalwareScan_UnknownMode_FallsBackToAudit()
    {
        // A typo must never resolve to the blocking mode.
        WriteJson("""{ "MalwareScan": { "Mode": "Enfroce" } }""");

        _sut.Load().MalwareScan.Mode.Should().Be("Audit");
    }

    [Fact]
    public void Load_MalwareScan_AllowedContentHashes_AppearInDoc()
    {
        WriteJson("""
            {
              "MalwareScan": {
                "AllowedContentHashes": [
                  { "Sha256": "aabbcc", "Note": "vendor macro sheet" },
                  { "Sha256": "ddeeff" }
                ]
              }
            }
            """);

        var hashes = _sut.Load().MalwareScan.AllowedContentHashes;

        hashes.Should().HaveCount(2);
        hashes[0].Sha256.Should().Be("aabbcc");
        hashes[0].Note.Should().Be("vendor macro sheet");
        hashes[1].Note.Should().BeNull();
    }

    [Fact]
    public void Load_MalwareScan_HashEntryWithoutSha256_IsDropped()
    {
        // An entry with no hash can never match anything; keeping it would only make the
        // ConfigTool show a row that does nothing.
        WriteJson("""
            { "MalwareScan": { "AllowedContentHashes": [ { "Note": "orphan" }, { "Sha256": "aabbcc" } ] } }
            """);

        _sut.Load().MalwareScan.AllowedContentHashes.Should().ContainSingle()
            .Which.Sha256.Should().Be("aabbcc");
    }

    [Fact]
    public void Load_MalwareScan_BypassLists_AppearInDoc()
    {
        WriteJson("""
            {
              "MalwareScan": {
                "BypassAuthenticatedUsers": [ "legacyapp" ],
                "BypassIpAddresses": [ "10.1.0.0/16", "192.168.0.5" ]
              }
            }
            """);

        var doc = _sut.Load().MalwareScan;

        doc.BypassAuthenticatedUsers.Should().Equal("legacyapp");
        doc.BypassIpAddresses.Should().Equal("10.1.0.0/16", "192.168.0.5");
    }

    [Fact]
    public void Load_MalwareScan_BypassIpComments_AppearInDoc()
    {
        // The comment is a ConfigTool concern only — kept in a parallel map so the runtime
        // option stays a plain string list, the same shape the IP whitelist uses.
        WriteJson("""
            {
              "MalwareScan": {
                "BypassIpAddresses": [ "10.1.0.0/16" ],
                "BypassIpComments": { "10.1.0.0/16": "ERP host, flags its own PDFs" }
              }
            }
            """);

        var doc = _sut.Load().MalwareScan;

        doc.BypassIpAddresses.Should().Equal("10.1.0.0/16");
        doc.BypassIpComments["10.1.0.0/16"].Should().Be("ERP host, flags its own PDFs");
    }

    [Fact]
    public void Load_MalwareScan_BypassIpCommentsAbsent_DefaultsToEmpty()
    {
        WriteJson("""{ "MalwareScan": { "BypassIpAddresses": [ "10.1.0.0/16" ] } }""");

        _sut.Load().MalwareScan.BypassIpComments.Should().BeEmpty();
    }

    [Fact]
    public void Load_MalwareScan_BypassListsAbsent_DefaultToEmpty()
    {
        WriteJson("""{ "Smtp": { "Banner": "test" } }""");

        var doc = _sut.Load().MalwareScan;

        doc.BypassAuthenticatedUsers.Should().BeEmpty();
        doc.BypassIpAddresses.Should().BeEmpty();
        doc.AllowedContentHashes.Should().BeEmpty();
    }
}
