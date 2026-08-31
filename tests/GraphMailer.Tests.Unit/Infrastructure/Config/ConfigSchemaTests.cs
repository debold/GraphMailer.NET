using System.Text.Json.Nodes;
using FluentAssertions;
using GraphMailer.Service.Infrastructure.Config;

namespace GraphMailer.Tests.Unit.Infrastructure.Config;

public sealed class ConfigSchemaTests
{
    [Fact]
    public void ReadVersion_Absent_IsZero()
        => ConfigSchema.ReadVersion(new JsonObject()).Should().Be(0);

    [Fact]
    public void ReadVersion_Present_IsValue()
        => ConfigSchema.ReadVersion(new JsonObject { ["SchemaVersion"] = 3 }).Should().Be(3);

    [Fact]
    public void Migrate_V0_RemovesObsoleteRetryKeys_AndStampsVersion()
    {
        var root = JsonNode.Parse("""{ "MailQueue": { "MaxRetries": 10, "RetryDelaySeconds": 60, "BatchSize": 5 } }""")!.AsObject();

        var changed = ConfigSchema.Migrate(root);

        changed.Should().BeTrue();
        ConfigSchema.ReadVersion(root).Should().Be(ConfigSchema.Current);
        var mq = root["MailQueue"]!.AsObject();
        mq.ContainsKey("MaxRetries").Should().BeFalse();
        mq.ContainsKey("RetryDelaySeconds").Should().BeFalse();
        mq.ContainsKey("BatchSize").Should().BeTrue("unrelated keys are preserved");
    }

    [Fact]
    public void Migrate_V1_ToCurrent_IsAdditiveOnly_ContentUnchangedExceptVersion()
    {
        // v2 only introduced Certificate.FailClosed (default false) — the migration is a
        // pure version stamp; existing content must survive byte-identical.
        var root = JsonNode.Parse("""{ "SchemaVersion": 1, "Certificate": { "SubjectName": "smtp.local" } }""")!.AsObject();

        var changed = ConfigSchema.Migrate(root);

        changed.Should().BeTrue();
        ConfigSchema.ReadVersion(root).Should().Be(ConfigSchema.Current);
        root["Certificate"]!.AsObject()["SubjectName"]!.GetValue<string>().Should().Be("smtp.local");
        root["Certificate"]!.AsObject().ContainsKey("FailClosed").Should().BeFalse(
            "the absent key is valid — the options binder falls back to the default (false)");
    }

    [Fact]
    public void Migrate_V2_ToV3_IsAdditiveOnly_ContentUnchangedExceptVersion()
    {
        // v3 only introduced UpdateCheck.Enabled and the UpdateAvailable notification type
        // (both default false) — the migration is a pure version stamp; existing content
        // must survive byte-identical.
        var root = JsonNode.Parse("""{ "SchemaVersion": 2, "Certificate": { "SubjectName": "smtp.local" } }""")!.AsObject();

        var changed = ConfigSchema.Migrate(root);

        changed.Should().BeTrue();
        ConfigSchema.ReadVersion(root).Should().Be(ConfigSchema.Current);
        root["Certificate"]!.AsObject()["SubjectName"]!.GetValue<string>().Should().Be("smtp.local");
        root.ContainsKey("UpdateCheck").Should().BeFalse(
            "the absent key is valid — the options binder falls back to the default (disabled)");
    }

    [Fact]
    public void Migrate_V3_ToV4_IsAdditiveOnly_ContentUnchangedExceptVersion()
    {
        // v4 only introduced Telemetry.Enabled (default false) — the migration is a pure
        // version stamp; existing content must survive byte-identical.
        var root = JsonNode.Parse("""{ "SchemaVersion": 3, "Certificate": { "SubjectName": "smtp.local" } }""")!.AsObject();

        var changed = ConfigSchema.Migrate(root);

        changed.Should().BeTrue();
        ConfigSchema.ReadVersion(root).Should().Be(ConfigSchema.Current);
        root["Certificate"]!.AsObject()["SubjectName"]!.GetValue<string>().Should().Be("smtp.local");
        root.ContainsKey("Telemetry").Should().BeFalse(
            "the absent key is valid — the options binder falls back to the default (disabled)");
    }

    [Fact]
    public void Migrate_V4_ToV5_IsAdditiveOnly_ContentUnchangedExceptVersion()
    {
        // v5 only introduced Recommendations.Dismissed (default empty) — the migration is a pure
        // version stamp; existing content must survive byte-identical.
        var root = JsonNode.Parse("""{ "SchemaVersion": 4, "Certificate": { "SubjectName": "smtp.local" } }""")!.AsObject();

        var changed = ConfigSchema.Migrate(root);

        changed.Should().BeTrue();
        ConfigSchema.ReadVersion(root).Should().Be(ConfigSchema.Current);
        root["Certificate"]!.AsObject()["SubjectName"]!.GetValue<string>().Should().Be("smtp.local");
        root.ContainsKey("Recommendations").Should().BeFalse(
            "the absent key is valid — the options binder falls back to the default (nothing hidden)");
    }

    [Fact]
    public void Migrate_V5_ToV6_MaterialisesAdminNotificationsEnabledFromTheRecipientCount()
    {
        // v6 turns AdminNotifications.Enabled from a value the ConfigTool derived on every save
        // into an authoritative setting. A hand-edited file without the key must not end up with
        // notifications silently off.
        var root = JsonNode.Parse("""
            { "SchemaVersion": 5, "AdminNotifications": { "RecipientAddresses": [ "ops@corp.com" ] } }
            """)!.AsObject();

        ConfigSchema.Migrate(root).Should().BeTrue();

        root["AdminNotifications"]!["Enabled"]!.GetValue<bool>().Should().BeTrue();
        ConfigSchema.ReadVersion(root).Should().Be(ConfigSchema.Current);
    }

    [Fact]
    public void Migrate_V5_ToV6_NoRecipients_LeavesAdminNotificationsDisabled()
    {
        var root = JsonNode.Parse("""
            { "SchemaVersion": 5, "AdminNotifications": { "RecipientAddresses": [] } }
            """)!.AsObject();

        ConfigSchema.Migrate(root);

        root["AdminNotifications"]!["Enabled"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public void Migrate_V5_ToV6_ExistingEnabledFlag_IsNotOverwritten()
    {
        // Every file the ConfigTool ever saved already carries the derived value — re-deriving it
        // would re-enable notifications somebody had switched off by hand.
        var root = JsonNode.Parse("""
            { "SchemaVersion": 5, "AdminNotifications": { "Enabled": false, "RecipientAddresses": [ "ops@corp.com" ] } }
            """)!.AsObject();

        ConfigSchema.Migrate(root);

        root["AdminNotifications"]!["Enabled"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public void Migrate_V5_ToV6_NoAdminNotificationsSection_IsLeftAlone()
    {
        var root = JsonNode.Parse("""{ "SchemaVersion": 5, "Smtp": { "Banner": "test" } }""")!.AsObject();

        ConfigSchema.Migrate(root);

        root.ContainsKey("AdminNotifications").Should().BeFalse();
        root["Smtp"]!["Banner"]!.GetValue<string>().Should().Be("test");
    }

    [Fact]
    public void Migrate_V6_ToV7_IsAdditiveOnly_ContentUnchangedExceptVersion()
    {
        // v7 only introduced the GraphCertificateExpiringWarning notification type (default on) —
        // the migration is a pure version stamp; existing content must survive byte-identical.
        var root = JsonNode.Parse("""{ "SchemaVersion": 6, "Certificate": { "SubjectName": "smtp.local" } }""")!.AsObject();

        var changed = ConfigSchema.Migrate(root);

        changed.Should().BeTrue();
        ConfigSchema.ReadVersion(root).Should().Be(ConfigSchema.Current);
        root["Certificate"]!.AsObject()["SubjectName"]!.GetValue<string>().Should().Be("smtp.local");
        root.ContainsKey("AdminNotifications").Should().BeFalse(
            "the absent key is valid — the options binder falls back to the default (warning on)");
    }

    [Fact]
    public void Migrate_V7_ToV8_AddsNoKeys_MaxRecipientsFallsBackToDefault()
    {
        // The additive half of v8: Smtp.MaxRecipients is absent and must stay absent —
        // the options binder falls back to 500, Exchange Online's own default.
        var root = JsonNode.Parse("""{ "SchemaVersion": 7, "Smtp": { "MaxSizeBytes": 26214400, "Banner": "test" } }""")!.AsObject();

        var changed = ConfigSchema.Migrate(root);

        changed.Should().BeTrue();
        ConfigSchema.ReadVersion(root).Should().Be(ConfigSchema.Current);
        root["Smtp"]!.AsObject().ContainsKey("MaxRecipients").Should().BeFalse(
            "the absent key is valid — the options binder falls back to the default of 500");
        root["Smtp"]!["MaxSizeBytes"]!.GetValue<long>().Should().Be(26_214_400);
        root["Smtp"]!["Banner"]!.GetValue<string>().Should().Be("test");
    }

    [Fact]
    public void Migrate_V7_ToV8_ZeroMaxSizeBytes_IsLiftedToExchangeCeiling()
    {
        // A config written by an older ConfigTool that documented 0 as "no limit". The
        // validator rejects 0, so the service never starts its listeners — the migration
        // has to heal it, otherwise the upgrade leaves the install dead.
        var root = JsonNode.Parse("""{ "SchemaVersion": 7, "Smtp": { "MaxSizeBytes": 0, "Banner": "test" } }""")!.AsObject();

        ConfigSchema.Migrate(root);

        root["Smtp"]!["MaxSizeBytes"]!.GetValue<long>().Should().Be(157_286_400,
            "0 meant 'no limit'; 150 MB is the largest value Exchange Online can actually deliver");
    }

    [Fact]
    public void Migrate_V7_ToV8_PositiveMaxSizeBytes_IsLeftAlone()
    {
        var root = JsonNode.Parse("""{ "SchemaVersion": 7, "Smtp": { "MaxSizeBytes": 10485760 } }""")!.AsObject();

        ConfigSchema.Migrate(root);

        root["Smtp"]!["MaxSizeBytes"]!.GetValue<long>().Should().Be(10_485_760,
            "a configured size is the operator's decision and must survive the migration");
    }

    [Fact]
    public void Migrate_V7_ToV8_NoSmtpSection_IsLeftAlone()
    {
        var root = JsonNode.Parse("""{ "SchemaVersion": 7, "Certificate": { "SubjectName": "smtp.local" } }""")!.AsObject();

        ConfigSchema.Migrate(root);

        root.ContainsKey("Smtp").Should().BeFalse("the migration must not materialise sections that were never there");
        ConfigSchema.ReadVersion(root).Should().Be(ConfigSchema.Current);
    }

    [Fact]
    public void Migrate_V8_ToV9_RemovesNotificationTypesThatNeverHadACaller()
    {
        var root = JsonNode.Parse("""
            {
              "SchemaVersion": 8,
              "AdminNotifications": {
                "NotificationTypes": {
                  "QueueProcessorFailure": { "Enabled": true },
                  "PortMonitoringSustainedOutage": { "Enabled": true },
                  "PortMonitoringAlert": { "Enabled": false }
                }
              }
            }
            """)!.AsObject();

        ConfigSchema.Migrate(root).Should().BeTrue();

        var types = root["AdminNotifications"]!["NotificationTypes"]!.AsObject();
        types.ContainsKey("QueueProcessorFailure").Should().BeFalse();
        types.ContainsKey("PortMonitoringSustainedOutage").Should().BeFalse();
        types["PortMonitoringAlert"]!["Enabled"]!.GetValue<bool>().Should().BeFalse(
            "a real switch the operator set must survive the clean-up");
    }

    [Fact]
    public void Migrate_V8_ToV9_RemovesUnreadPortAlertCooldown()
    {
        var root = JsonNode.Parse("""
            { "SchemaVersion": 8, "PortMonitoring": { "CheckIntervalMinutes": 3, "AlertCooldownMinutes": 60 } }
            """)!.AsObject();

        ConfigSchema.Migrate(root);

        var port = root["PortMonitoring"]!.AsObject();
        port.ContainsKey("AlertCooldownMinutes").Should().BeFalse("no code ever read it");
        port["CheckIntervalMinutes"]!.GetValue<int>().Should().Be(3);
    }

    [Fact]
    public void Migrate_V8_ToV9_SilencedRecoveryToggle_CarriesOverToGlobalSwitch()
    {
        // The two per-monitor recovery toggles are folded into one global switch. An operator who
        // hand-edited either to false must not start receiving all-clear mails after the upgrade.
        var root = JsonNode.Parse("""
            {
              "SchemaVersion": 8,
              "AdminNotifications": {
                "NotificationTypes": { "PortMonitoringRecovery": { "Enabled": false } }
              }
            }
            """)!.AsObject();

        ConfigSchema.Migrate(root);

        root["AdminNotifications"]!["SendRecoveryNotification"]!.GetValue<bool>().Should().BeFalse();
        root["AdminNotifications"]!["NotificationTypes"]!.AsObject()
            .ContainsKey("PortMonitoringRecovery").Should().BeFalse();
    }

    [Fact]
    public void Migrate_V8_ToV9_RecoveryTogglesLeftAtDefault_DoesNotWriteGlobalSwitch()
    {
        var root = JsonNode.Parse("""
            {
              "SchemaVersion": 8,
              "AdminNotifications": {
                "NotificationTypes": {
                  "PortMonitoringRecovery": { "Enabled": true },
                  "GraphApiConnectivityRestored": { "Enabled": true }
                }
              }
            }
            """)!.AsObject();

        ConfigSchema.Migrate(root);

        root["AdminNotifications"]!.AsObject().ContainsKey("SendRecoveryNotification").Should().BeFalse(
            "the absent key is valid — the options binder falls back to the default (all-clear on)");
    }

    [Fact]
    public void Migrate_V8_ToV9_AddsNoKeys_RepeatSettingsFallBackToDefaults()
    {
        var root = JsonNode.Parse("""{ "SchemaVersion": 8, "AdminNotifications": { "SubjectPrefix": "[GM]" } }""")!.AsObject();

        ConfigSchema.Migrate(root).Should().BeTrue();

        var notifications = root["AdminNotifications"]!.AsObject();
        notifications["SubjectPrefix"]!.GetValue<string>().Should().Be("[GM]");
        notifications.ContainsKey("RenotifyMinutes").Should().BeFalse(
            "the absent key is valid — the options binder falls back to the default (1440)");
    }

    [Fact]
    public void Migrate_V8_ToV9_NoAdminNotificationsSection_IsLeftAlone()
    {
        var root = JsonNode.Parse("""{ "SchemaVersion": 8, "Certificate": { "SubjectName": "smtp.local" } }""")!.AsObject();

        ConfigSchema.Migrate(root);

        root.ContainsKey("AdminNotifications").Should().BeFalse(
            "the migration must not materialise sections that were never there");
        ConfigSchema.ReadVersion(root).Should().Be(ConfigSchema.Current);
    }

    [Fact]
    public void Migrate_AlreadyCurrent_IsNoOp()
        => ConfigSchema.Migrate(new JsonObject { ["SchemaVersion"] = ConfigSchema.Current }).Should().BeFalse();

    [Fact]
    public void Migrate_Idempotent()
    {
        var root = JsonNode.Parse("""{ "MailQueue": { "MaxRetries": 10 } }""")!.AsObject();

        ConfigSchema.Migrate(root).Should().BeTrue();
        ConfigSchema.Migrate(root).Should().BeFalse("a second run finds it already current");
    }

    [Fact]
    public void Migrate_NewerThanBuild_LeavesFileAlone()
    {
        var root = new JsonObject { ["SchemaVersion"] = ConfigSchema.Current + 1 };

        ConfigSchema.Migrate(root).Should().BeFalse();
        ConfigSchema.ReadVersion(root).Should().Be(ConfigSchema.Current + 1);
    }
}

public sealed class ConfigMigratorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gm-cfgmig-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;

    public ConfigMigratorTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "graphmailer.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void MigrateFile_OldFile_Migrates_BacksUp_AndStamps()
    {
        File.WriteAllText(_file, """{ "MailQueue": { "MaxRetries": 10, "RetryDelaySeconds": 60 } }""");

        var r = ConfigMigrator.MigrateFile(_file);

        r.Migrated.Should().BeTrue();
        r.From.Should().Be(0);
        r.To.Should().Be(ConfigSchema.Current);
        r.BackupPath.Should().NotBeNull();
        File.Exists(r.BackupPath!).Should().BeTrue("the original is backed up before rewriting");

        var root = JsonNode.Parse(File.ReadAllText(_file))!.AsObject();
        ConfigSchema.ReadVersion(root).Should().Be(ConfigSchema.Current);
        root["MailQueue"]!.AsObject().ContainsKey("MaxRetries").Should().BeFalse();
    }

    [Fact]
    public void MigrateFile_CurrentFile_IsNoOp()
    {
        File.WriteAllText(_file, $$"""{ "SchemaVersion": {{ConfigSchema.Current}}, "MailQueue": {} }""");

        ConfigMigrator.MigrateFile(_file).Migrated.Should().BeFalse();
    }

    [Fact]
    public void MigrateFile_NewerFile_IsIncompatible_AndUnchanged()
    {
        var content = $$"""{ "SchemaVersion": {{ConfigSchema.Current + 1}} }""";
        File.WriteAllText(_file, content);

        var r = ConfigMigrator.MigrateFile(_file);

        r.Incompatible.Should().BeTrue();
        r.Migrated.Should().BeFalse();
        File.ReadAllText(_file).Should().Be(content, "a config from a newer build is left untouched");
    }

    [Fact]
    public void MigrateFile_MissingFile_IsNoOp()
        => ConfigMigrator.MigrateFile(Path.Combine(_dir, "absent.json")).Migrated.Should().BeFalse();

    [Fact]
    public void MigrateFile_InvalidJson_IsLeftAlone()
    {
        File.WriteAllText(_file, "{ not valid json");

        ConfigMigrator.MigrateFile(_file).Migrated.Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // Corrupt-config quarantine (service starts on defaults instead of crashing)
    // -------------------------------------------------------------------------

    [Fact]
    public void QuarantineIfCorrupt_InvalidJson_MovesFileAsideAndReturnsPath()
    {
        File.WriteAllText(_file, "{ truncated");

        var quarantine = ConfigMigrator.QuarantineIfCorrupt(_file);

        quarantine.Should().NotBeNull();
        File.Exists(_file).Should().BeFalse("the corrupt file must be out of the config path so startup succeeds");
        File.Exists(quarantine!).Should().BeTrue("the original content is preserved for repair");
        File.ReadAllText(quarantine!).Should().Be("{ truncated");
    }

    [Fact]
    public void QuarantineIfCorrupt_ValidJson_IsNoOp()
    {
        File.WriteAllText(_file, """{ "SchemaVersion": 2 }""");

        ConfigMigrator.QuarantineIfCorrupt(_file).Should().BeNull();
        File.Exists(_file).Should().BeTrue();
    }

    [Fact]
    public void QuarantineIfCorrupt_MissingFile_IsNoOp()
        => ConfigMigrator.QuarantineIfCorrupt(Path.Combine(_dir, "absent.json")).Should().BeNull();

    // -------------------------------------------------------------------------
    // v9 → v10: MalwareScan (additive)
    // -------------------------------------------------------------------------

    [Fact]
    public void Migrate_V9_ToCurrent_LeavesMalwareScanAbsent()
    {
        // Purely additive: no key is written. Materialising a mode into an existing file
        // would be a policy decision the operator never made — and the binder's default
        // (Audit) is the safe one anyway.
        var root = JsonNode.Parse("""{ "SchemaVersion": 9, "Smtp": { "Banner": "keep me" } }""")!.AsObject();

        var changed = ConfigSchema.Migrate(root);

        changed.Should().BeTrue();
        ConfigSchema.ReadVersion(root).Should().Be(ConfigSchema.Current);
        root["Smtp"]!.AsObject()["Banner"]!.GetValue<string>().Should().Be("keep me");
        root.ContainsKey("MalwareScan").Should().BeFalse(
            "the absent section is valid — the options binder falls back to Audit mode");
    }

    [Fact]
    public void Migrate_V9_ToCurrent_PreservesAnExistingMalwareScanSection()
    {
        // A hand-written section (or one from a newer build) must survive untouched.
        var root = JsonNode.Parse("""
            { "SchemaVersion": 9, "MalwareScan": { "Mode": "Enforce", "TimeoutSeconds": 45 } }
            """)!.AsObject();

        ConfigSchema.Migrate(root);

        var section = root["MalwareScan"]!.AsObject();
        section["Mode"]!.GetValue<string>().Should().Be("Enforce");
        section["TimeoutSeconds"]!.GetValue<int>().Should().Be(45);
    }

    // -------------------------------------------------------------------------
    // Migration-backup pruning (config\backups\ must not grow forever)
    // -------------------------------------------------------------------------

    [Fact]
    public void PruneMigrationBackups_KeepsOnlyTheNewestTen()
    {
        var backupDir = Path.Combine(_dir, "backups");
        Directory.CreateDirectory(backupDir);
        for (var i = 0; i < 13; i++)
        {
            var path = Path.Combine(backupDir, $"graphmailer.json.v1-{i:00}.bak");
            File.WriteAllText(path, "{}");
            File.SetCreationTimeUtc(path, DateTime.UtcNow.AddMinutes(-13 + i));
        }

        ConfigMigrator.PruneMigrationBackups(backupDir);

        var remaining = Directory.GetFiles(backupDir, "*.bak").Select(Path.GetFileName).ToList();
        remaining.Should().HaveCount(10);
        remaining.Should().NotContain("graphmailer.json.v1-00.bak", "the oldest backups are pruned first");
        remaining.Should().Contain("graphmailer.json.v1-12.bak", "the newest backup is always kept");
    }

    // =========================================================================
    // v10 → v11: MessageRules
    // =========================================================================

    [Fact]
    public void Migrate_V10_ToCurrent_LeavesMessageRulesAbsent()
    {
        // Purely additive. Materialising the section into an existing file would suggest a
        // policy the operator never configured; the binder's defaults already relay exactly
        // as the installation did before the upgrade.
        var root = JsonNode.Parse("""{ "SchemaVersion": 10, "Smtp": { "Banner": "keep me" } }""")!.AsObject();

        var changed = ConfigSchema.Migrate(root);

        changed.Should().BeTrue();
        ConfigSchema.ReadVersion(root).Should().Be(ConfigSchema.Current);
        root["Smtp"]!.AsObject()["Banner"]!.GetValue<string>().Should().Be("keep me");
        root.ContainsKey("MessageRules").Should().BeFalse(
            "the absent section is valid — the binder falls back to a disabled rule engine");
    }

    [Fact]
    public void Migrate_V10_ToCurrent_PreservesAnExistingMessageRulesSection()
    {
        var root = JsonNode.Parse("""
            {
              "SchemaVersion": 10,
              "MessageRules": {
                "Enabled": true,
                "Rules": [ { "Name": "keep", "Actions": [ { "Type": "Discard" } ] } ]
              }
            }
            """)!.AsObject();

        ConfigSchema.Migrate(root).Should().BeTrue();

        var section = root["MessageRules"]!.AsObject();
        section["Enabled"]!.GetValue<bool>().Should().BeTrue();
        section["Rules"]!.AsArray().Should().ContainSingle();
    }

    [Fact]
    public void Migrate_CurrentVersion_IsANoOp()
    {
        var root = JsonNode.Parse($$"""{ "SchemaVersion": {{ConfigSchema.Current}}, "Smtp": { "Banner": "x" } }""")!.AsObject();

        ConfigSchema.Migrate(root).Should().BeFalse("a file already at the current version needs no migration");
    }
}
