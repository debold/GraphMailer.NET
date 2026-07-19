using FluentAssertions;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Config;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace GraphMailer.Tests.Unit.Infrastructure.Config;

/// <summary>
/// Contract tests that verify ConfigService writes the correct JSON keys for every
/// setting that the service reads via IConfiguration / Options binding.
///
/// Each test follows the same pattern:
///   1. Build a ConfigDocument with non-default values for the section under test.
///   2. Save it via ConfigService.
///   3. Load the resulting JSON file using Microsoft.Extensions.Configuration.Json
///      (the same mechanism the service uses at runtime).
///   4. Bind the relevant Options class and assert every field matches.
///
/// A failing test here means either ConfigService writes a wrong key or a wrong
/// section name, and the service would silently fall back to its defaults at runtime.
/// </summary>
public sealed class ConfigSchemaBindingTests : IDisposable
{
    private readonly string _dir;
    private readonly string _filePath;
    private readonly ConfigService _sut;

    public ConfigSchemaBindingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"gm-binding-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _filePath = Path.Combine(_dir, "graphmailer.json");
        var protector = new EphemeralDataProtectionProvider().CreateProtector("GraphMailer.Config");
        _sut = new ConfigService(_filePath, protector);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private IConfigurationRoot LoadServiceConfig()
        => new ConfigurationBuilder()
            .AddJsonFile(_filePath, optional: false, reloadOnChange: false)
            .Build();

    private static T Bind<T>(IConfiguration config, string sectionName) where T : new()
    {
        var instance = new T();
        config.GetSection(sectionName).Bind(instance);
        return instance;
    }

    // =========================================================================
    // Certificate
    // =========================================================================

    [Fact]
    public void Save_FailedEmailRetentionDays_BindsToMailQueueFailedEmailRetentionDays()
    {
        _sut.Save(new ConfigDocument { MailQueue = new() { FailedEmailRetentionDays = 14 } });

        var opts = Bind<MailQueueOptions>(LoadServiceConfig(), MailQueueOptions.SectionName);

        opts.FailedEmailRetentionDays.Should().Be(14);
    }

    [Fact]
    public void Save_CertificateFailClosed_BindsToCertificateFailClosed()
    {
        _sut.Save(new ConfigDocument { Certificate = new() { FailClosed = true } });

        var opts = Bind<CertificateOptions>(LoadServiceConfig(), CertificateOptions.SectionName);

        opts.FailClosed.Should().BeTrue();
    }

    // =========================================================================
    // CertificateMonitoring
    // =========================================================================

    [Fact]
    public void Save_CertWarnDays_BindsToCertificateMonitoringWarningThresholdDays()
    {
        _sut.Save(new ConfigDocument { Monitoring = new() { CertWarnDays = 21 } });

        var opts = Bind<CertificateMonitoringOptions>(
            LoadServiceConfig(), CertificateMonitoringOptions.SectionName);

        opts.WarningThresholdDays.Should().Be(21);
    }

    // =========================================================================
    // DiskSpaceMonitoring
    // =========================================================================

    [Fact]
    public void Save_DiskWarnPct_BindsToDiskSpaceMonitoringThresholdPercent()
    {
        _sut.Save(new ConfigDocument { Monitoring = new() { DiskWarnPct = 20 } });

        var opts = Bind<DiskSpaceMonitoringOptions>(
            LoadServiceConfig(), DiskSpaceMonitoringOptions.SectionName);

        opts.ThresholdPercent.Should().Be(20);
    }

    // =========================================================================
    // PortMonitoring
    // =========================================================================

    [Fact]
    public void Save_PortCheckIntervalMinutes_BindsToPortMonitoringCheckIntervalMinutes()
    {
        _sut.Save(new ConfigDocument { Monitoring = new() { PortCheckIntervalMinutes = 3 } });

        var opts = Bind<PortMonitoringOptions>(
            LoadServiceConfig(), PortMonitoringOptions.SectionName);

        opts.CheckIntervalMinutes.Should().Be(3);
    }

    // =========================================================================
    // GraphApiMonitoring
    // =========================================================================

    [Fact]
    public void Save_GraphCheckIntervalMinutes_BindsToGraphApiMonitoringCheckIntervalMinutes()
    {
        _sut.Save(new ConfigDocument { Monitoring = new() { GraphCheckIntervalMinutes = 30 } });

        var opts = Bind<GraphApiMonitoringOptions>(
            LoadServiceConfig(), GraphApiMonitoringOptions.SectionName);

        opts.CheckIntervalMinutes.Should().Be(30);
    }

    // =========================================================================
    // AdminNotifications – address fields
    // =========================================================================

    [Fact]
    public void Save_RecipientAddresses_BindToAdminNotificationsRecipientAddresses()
    {
        _sut.Save(new ConfigDocument
        {
            Notification = new() { RecipientAddresses = ["admin@corp.com"], NotifFrom = "relay@corp.com" }
        });

        var opts = Bind<AdminNotificationsOptions>(
            LoadServiceConfig(), AdminNotificationsOptions.SectionName);

        opts.RecipientAddresses.Should().ContainSingle("admin@corp.com");
        opts.SenderAddress.Should().Be("relay@corp.com");
        opts.Enabled.Should().BeTrue("Enabled is derived from RecipientAddresses being non-empty");
    }

    [Fact]
    public void Save_RecipientAddressesEmpty_DisablesAdminNotifications()
    {
        _sut.Save(new ConfigDocument
        {
            Notification = new() { RecipientAddresses = [], NotifFrom = null }
        });

        var opts = Bind<AdminNotificationsOptions>(
            LoadServiceConfig(), AdminNotificationsOptions.SectionName);

        opts.Enabled.Should().BeFalse();
        opts.RecipientAddresses.Should().BeEmpty();
    }

    [Fact]
    public void Save_SubjectPrefix_BindsToAdminNotificationsSubjectPrefix()
    {
        _sut.Save(new ConfigDocument
        {
            Notification = new() { SubjectPrefix = "[TEST]" }
        });

        var opts = Bind<AdminNotificationsOptions>(
            LoadServiceConfig(), AdminNotificationsOptions.SectionName);

        opts.SubjectPrefix.Should().Be("[TEST]");
    }

    [Fact]
    public void Save_ScheduledReport_BindsToAdminNotificationsScheduledReport()
    {
        _sut.Save(new ConfigDocument
        {
            Notification = new()
            {
                ReportEnabled = true,
                ReportFrequency = "Monthly",
                ReportTimeOfDay = "08:30",
                ReportDayOfWeek = "Friday",
                ReportDayOfMonth = 5,
            }
        });

        var opts = Bind<AdminNotificationsOptions>(
            LoadServiceConfig(), AdminNotificationsOptions.SectionName);

        opts.ScheduledReport.Enabled.Should().BeTrue();
        opts.ScheduledReport.Frequency.Should().Be(ReportFrequency.Monthly);
        opts.ScheduledReport.TimeOfDay.Should().Be("08:30");
        opts.ScheduledReport.DayOfWeek.Should().Be(DayOfWeek.Friday);
        opts.ScheduledReport.DayOfMonth.Should().Be(5);
    }

    // =========================================================================
    // AdminNotifications – NotificationTypes toggles
    // =========================================================================

    [Fact]
    public void Save_NotifIpBlocked_False_BindsToIpBlockedAlertEnabled_False()
    {
        _sut.Save(new ConfigDocument
        {
            Notification = new() { NotifIpBlocked = false }
        });

        var opts = Bind<AdminNotificationsOptions>(
            LoadServiceConfig(), AdminNotificationsOptions.SectionName);

        opts.NotificationTypes.IpBlockedAlert.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Save_NotifDeliveryFailed_False_BindsToEmailDeliveryFailedEnabled_False()
    {
        _sut.Save(new ConfigDocument
        {
            Notification = new() { NotifDeliveryFailed = false }
        });

        var opts = Bind<AdminNotificationsOptions>(
            LoadServiceConfig(), AdminNotificationsOptions.SectionName);

        opts.NotificationTypes.EmailDeliveryFailed.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Save_NotifCertExpiring_False_BindsToCertificateExpiringWarningEnabled_False()
    {
        _sut.Save(new ConfigDocument
        {
            Notification = new() { NotifCertExpiring = false }
        });

        var opts = Bind<AdminNotificationsOptions>(
            LoadServiceConfig(), AdminNotificationsOptions.SectionName);

        opts.NotificationTypes.CertificateExpiringWarning.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Save_NotifCertExpired_False_BindsToCertificateExpiredEnabled_False()
    {
        _sut.Save(new ConfigDocument
        {
            Notification = new() { NotifCertExpired = false }
        });

        var opts = Bind<AdminNotificationsOptions>(
            LoadServiceConfig(), AdminNotificationsOptions.SectionName);

        opts.NotificationTypes.CertificateExpired.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Save_NotifDiskSpace_False_BindsToLowDiskSpaceWarningEnabled_False()
    {
        _sut.Save(new ConfigDocument
        {
            Notification = new() { NotifDiskSpace = false }
        });

        var opts = Bind<AdminNotificationsOptions>(
            LoadServiceConfig(), AdminNotificationsOptions.SectionName);

        opts.NotificationTypes.LowDiskSpaceWarning.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Save_NotifGraphDown_False_BindsToGraphApiConnectionErrorEnabled_False()
    {
        _sut.Save(new ConfigDocument
        {
            Notification = new() { NotifGraphDown = false }
        });

        var opts = Bind<AdminNotificationsOptions>(
            LoadServiceConfig(), AdminNotificationsOptions.SectionName);

        opts.NotificationTypes.GraphApiConnectionError.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Save_NotifPortDown_False_BindsToPortMonitoringAlertEnabled_False()
    {
        _sut.Save(new ConfigDocument
        {
            Notification = new() { NotifPortDown = false }
        });

        var opts = Bind<AdminNotificationsOptions>(
            LoadServiceConfig(), AdminNotificationsOptions.SectionName);

        opts.NotificationTypes.PortMonitoringAlert.Enabled.Should().BeFalse();
    }

    // =========================================================================
    // SmtpOptions – AuthMode backward compatibility
    // =========================================================================

    [Fact]
    public void Save_AuthModeRequired_BindsToSmtpServerEntryAuthRequired_True()
    {
        _sut.Save(new ConfigDocument
        {
            Servers = [new() { Port = 587, Mode = "StartTls", AuthMode = "Required" }]
        });

        var config = LoadServiceConfig();
        var servers = config.GetSection("Servers").Get<List<SmtpServerEntry>>() ?? [];

        servers.Should().ContainSingle()
            .Which.AuthRequired.Should().BeTrue();
    }

    [Fact]
    public void Save_AuthModeNone_BindsToSmtpServerEntryAuthRequired_False()
    {
        _sut.Save(new ConfigDocument
        {
            Servers = [new() { Port = 2525, Mode = "Plain", AuthMode = "None" }]
        });

        var config = LoadServiceConfig();
        var servers = config.GetSection("Servers").Get<List<SmtpServerEntry>>() ?? [];

        servers.Should().ContainSingle()
            .Which.AuthRequired.Should().BeFalse();
    }

    // =========================================================================
    // Round-trip: ConfigDocument → Save → IConfiguration → Bind → matches back
    // (meta-test: verifies all sections together)
    // =========================================================================

    [Fact]
    public void Save_AllMonitoringAndNotificationFields_BindCorrectlyAsServiceOptions()
    {
        var doc = new ConfigDocument
        {
            Monitoring = new()
            {
                CertWarnDays = 7,
                DiskWarnPct = 15,
                PortCheckIntervalMinutes = 10,
                GraphCheckIntervalMinutes = 20,
            },
            Notification = new()
            {
                RecipientAddresses = ["ops@corp.com"],
                NotifFrom = "noreply@corp.com",
                SubjectPrefix = "[ALERT]",
                NotifIpBlocked = false,
                NotifDeliveryFailed = false,
                NotifCertExpiring = false,
                NotifCertExpired = false,
                NotifDiskSpace = false,
                NotifGraphDown = false,
                NotifPortDown = false,
            }
        };
        _sut.Save(doc);

        var config = LoadServiceConfig();

        Bind<CertificateMonitoringOptions>(config, CertificateMonitoringOptions.SectionName)
            .WarningThresholdDays.Should().Be(7);
        Bind<DiskSpaceMonitoringOptions>(config, DiskSpaceMonitoringOptions.SectionName)
            .ThresholdPercent.Should().Be(15);
        Bind<PortMonitoringOptions>(config, PortMonitoringOptions.SectionName)
            .CheckIntervalMinutes.Should().Be(10);
        Bind<GraphApiMonitoringOptions>(config, GraphApiMonitoringOptions.SectionName)
            .CheckIntervalMinutes.Should().Be(20);

        var notif = Bind<AdminNotificationsOptions>(config, AdminNotificationsOptions.SectionName);
        notif.RecipientAddresses.Should().ContainSingle("ops@corp.com");
        notif.SenderAddress.Should().Be("noreply@corp.com");
        notif.SubjectPrefix.Should().Be("[ALERT]");
        notif.NotificationTypes.IpBlockedAlert.Enabled.Should().BeFalse();
        notif.NotificationTypes.EmailDeliveryFailed.Enabled.Should().BeFalse();
        notif.NotificationTypes.CertificateExpiringWarning.Enabled.Should().BeFalse();
        notif.NotificationTypes.CertificateExpired.Enabled.Should().BeFalse();
        notif.NotificationTypes.LowDiskSpaceWarning.Enabled.Should().BeFalse();
        notif.NotificationTypes.GraphApiConnectionError.Enabled.Should().BeFalse();
        notif.NotificationTypes.PortMonitoringAlert.Enabled.Should().BeFalse();
    }

    // =========================================================================
    // MailQueue
    // =========================================================================

    [Fact]
    public void Save_MailQueueMailDir_BindsToMailQueueOptionsMailDir()
    {
        _sut.Save(new ConfigDocument
        {
            MailQueue = new() { MailDir = "D:\\MailStorage" }
        });

        var opts = Bind<MailQueueOptions>(LoadServiceConfig(), MailQueueOptions.SectionName);

        opts.MailDir.Should().Be("D:\\MailStorage");
    }

    [Fact]
    public void Save_MailQueueRetryPolicy_BindsToMailQueueOptions()
    {
        _sut.Save(new ConfigDocument
        {
            MailQueue = new()
            {
                TransientRetryCount = 4,
                TransientRetryIntervalSeconds = 120,
                RetryIntervalSeconds = 600,
                MessageExpirationHours = 48,
            }
        });

        var opts = Bind<MailQueueOptions>(LoadServiceConfig(), MailQueueOptions.SectionName);

        opts.TransientRetryCount.Should().Be(4);
        opts.TransientRetryIntervalSeconds.Should().Be(120);
        opts.RetryIntervalSeconds.Should().Be(600);
        opts.MessageExpirationHours.Should().Be(48);
    }

    // =========================================================================
    // Metrics  (SectionName = "Metrics")
    // =========================================================================

    [Fact]
    public void Save_MetricsEnabled_False_BindsToMetricsOptionsEnabled_False()
    {
        _sut.Save(new ConfigDocument { Metrics = new() { Enabled = false } });

        var opts = Bind<MetricsOptions>(LoadServiceConfig(), MetricsOptions.SectionName);

        opts.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Save_MetricsRetentionDays_BindsToMetricsOptionsRetentionDays()
    {
        _sut.Save(new ConfigDocument { Metrics = new() { RetentionDays = 30 } });

        var opts = Bind<MetricsOptions>(LoadServiceConfig(), MetricsOptions.SectionName);

        opts.RetentionDays.Should().Be(30);
    }

    [Fact]
    public void Save_MetricsCleanupIntervalHours_BindsToMetricsOptionsCleanupIntervalHours()
    {
        _sut.Save(new ConfigDocument { Metrics = new() { CleanupIntervalHours = 12 } });

        var opts = Bind<MetricsOptions>(LoadServiceConfig(), MetricsOptions.SectionName);

        opts.CleanupIntervalHours.Should().Be(12);
    }

    [Fact]
    public void Save_MetricsPerfEnabled_False_BindsToPerformanceMetricsOptionsEnabled_False()
    {
        _sut.Save(new ConfigDocument { Metrics = new() { PerfMetricsEnabled = false } });

        var opts = Bind<MetricsOptions>(LoadServiceConfig(), MetricsOptions.SectionName);

        opts.PerformanceMetrics.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Save_MetricsPerfMemoryIntervalSeconds_BindsToPerformanceMetricsOptionsMemoryIntervalSeconds()
    {
        _sut.Save(new ConfigDocument { Metrics = new() { PerfMemoryIntervalSeconds = 120 } });

        var opts = Bind<MetricsOptions>(LoadServiceConfig(), MetricsOptions.SectionName);

        opts.PerformanceMetrics.MemoryIntervalSeconds.Should().Be(120);
    }

    [Fact]
    public void Save_MetricsPerfCpuIntervalSeconds_BindsToPerformanceMetricsOptionsCpuIntervalSeconds()
    {
        _sut.Save(new ConfigDocument { Metrics = new() { PerfCpuIntervalSeconds = 30 } });

        var opts = Bind<MetricsOptions>(LoadServiceConfig(), MetricsOptions.SectionName);

        opts.PerformanceMetrics.CpuIntervalSeconds.Should().Be(30);
    }

    [Fact]
    public void Save_MetricsPerfDiskIntervalSeconds_BindsToPerformanceMetricsOptionsDiskIntervalSeconds()
    {
        _sut.Save(new ConfigDocument { Metrics = new() { PerfDiskIntervalSeconds = 600 } });

        var opts = Bind<MetricsOptions>(LoadServiceConfig(), MetricsOptions.SectionName);

        opts.PerformanceMetrics.DiskIntervalSeconds.Should().Be(600);
    }

    // =========================================================================
    // Users (SmtpAccessOptions binds at the config root)
    // =========================================================================

    [Fact]
    public void Save_DisabledUser_BindsToUserEntryEnabledFalse()
    {
        // Regression: the runtime UserEntry was missing the Enabled property,
        // so users disabled in the ConfigTool could still authenticate.
        _sut.Save(new ConfigDocument
        {
            Access = new()
            {
                Users = [new ConfigDocument.UserEntry { Username = "alice", Password = "pw", Enabled = false }],
            },
        });

        var opts = new SmtpAccessOptions();
        LoadServiceConfig().Bind(opts);

        opts.Users.Should().ContainSingle();
        opts.Users[0].Username.Should().Be("alice");
        opts.Users[0].Enabled.Should().BeFalse();
    }

    // =========================================================================
    // UpdateCheck
    // =========================================================================

    [Fact]
    public void Save_UpdateCheckEnabled_BindsToUpdateCheckEnabled()
    {
        _sut.Save(new ConfigDocument { Monitoring = new() { UpdateCheckEnabled = true } });

        var opts = Bind<UpdateCheckOptions>(LoadServiceConfig(), UpdateCheckOptions.SectionName);

        opts.Enabled.Should().BeTrue();
    }

    // =========================================================================
    // Telemetry
    // =========================================================================

    [Fact]
    public void Save_TelemetryEnabled_BindsToTelemetryOptionsEnabled()
    {
        _sut.Save(new ConfigDocument { Monitoring = new() { TelemetryEnabled = true } });

        var opts = Bind<TelemetryOptions>(LoadServiceConfig(), TelemetryOptions.SectionName);

        opts.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Save_TelemetryDisabled_BindsToTelemetryOptionsDisabled()
    {
        _sut.Save(new ConfigDocument { Monitoring = new() { TelemetryEnabled = false } });

        var opts = Bind<TelemetryOptions>(LoadServiceConfig(), TelemetryOptions.SectionName);

        opts.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Save_NotifUpdateAvailable_True_BindsToUpdateAvailableEnabled_True()
    {
        _sut.Save(new ConfigDocument { Notification = new() { NotifUpdateAvailable = true } });

        var notif = Bind<AdminNotificationsOptions>(LoadServiceConfig(), AdminNotificationsOptions.SectionName);

        notif.NotificationTypes.UpdateAvailable.Enabled.Should().BeTrue();
    }

    // =========================================================================
    // Backup
    // =========================================================================

    [Fact]
    public void Save_NotifBackup_False_BindsToBackupResultEnabled_False()
    {
        _sut.Save(new ConfigDocument { Notification = new() { NotifBackup = false } });

        var notif = Bind<AdminNotificationsOptions>(LoadServiceConfig(), AdminNotificationsOptions.SectionName);

        notif.NotificationTypes.BackupResult.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Backup_AllFields_BindToBackupOptions()
    {
        _sut.Save(new ConfigDocument
        {
            Backup = new ConfigDocument.BackupSection
            {
                BackupEnabled = true,
                Frequency = "Weekly",
                TimeOfDay = "04:15",
                DayOfWeek = "Wednesday",
                MaxBackups = 7,
                Directory = @"D:\backups",
                Password = "backup-secret",
                EmailEnabled = true,
                EmailRecipients = ["ops@corp.com", "admin@corp.com"],
            },
        });

        var opts = Bind<BackupOptions>(LoadServiceConfig(), BackupOptions.SectionName);

        opts.Enabled.Should().BeTrue();
        opts.Frequency.Should().Be(BackupFrequency.Weekly);
        opts.TimeOfDay.Should().Be("04:15");
        opts.DayOfWeek.Should().Be(DayOfWeek.Wednesday);
        opts.MaxBackups.Should().Be(7);
        opts.Directory.Should().Be(@"D:\backups");
        opts.Email.Enabled.Should().BeTrue();
        opts.Email.Recipients.Should().BeEquivalentTo("ops@corp.com", "admin@corp.com");
    }

    [Fact]
    public void Backup_Password_IsWrittenEncrypted()
    {
        _sut.Save(new ConfigDocument
        {
            Backup = new ConfigDocument.BackupSection { Password = "backup-secret" },
        });

        var raw = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_filePath))!;
        raw["Backup"]!["Password"]!.GetValue<string>().Should().StartWith("ENC[").And.EndWith("]");
    }
}
