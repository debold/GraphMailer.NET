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
}
