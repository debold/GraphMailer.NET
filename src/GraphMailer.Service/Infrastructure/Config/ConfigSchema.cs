using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace GraphMailer.Service.Infrastructure.Config;

/// <summary>
/// Versioned, forward-only migration of <c>graphmailer.json</c>. The file carries a top-level
/// <c>"SchemaVersion"</c>; an upgraded binary migrates an older file up to <see cref="Current"/>
/// (rename / move / drop keys) before it is read. Migrations operate on the raw JSON structure
/// only, so encrypted <c>ENC[…]</c> values are never touched.
///
/// To add a schema change: bump <see cref="Current"/>, add a <c>MigrateToN</c> step + its call
/// in <see cref="Migrate"/>, and add tests. Never change the config shape silently.
/// </summary>
internal static class ConfigSchema
{
    /// <summary>Config schema version understood by this build.</summary>
    internal const int Current = 7;

    internal const string VersionKey = "SchemaVersion";

    /// <summary>On-disk schema version; <c>0</c> when the marker is absent (pre-versioning files).</summary>
    internal static int ReadVersion(JsonObject root)
        => root[VersionKey] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    /// <summary>
    /// Migrates <paramref name="root"/> in place up to <see cref="Current"/> and stamps the version.
    /// Returns <see langword="true"/> when the file content changed. No-op when already at (or above)
    /// the current version — callers detect "newer than this build" via <see cref="ReadVersion"/>.
    /// </summary>
    internal static bool Migrate(JsonObject root)
    {
        var from = ReadVersion(root);
        if (from >= Current) return false;

        if (from < 1) MigrateTo1(root);
        if (from < 2) MigrateTo2(root);
        if (from < 3) MigrateTo3(root);
        if (from < 4) MigrateTo4(root);
        if (from < 5) MigrateTo5(root);
        if (from < 6) MigrateTo6(root);
        if (from < 7) MigrateTo7(root);
        // if (from < 8) MigrateTo8(root);   // future steps go here, in order

        root[VersionKey] = Current;
        return true;
    }

    /// <summary>
    /// v0 → v1: the mail-queue retry policy changed from a count-based exponential model
    /// (<c>MaxRetries</c> / <c>RetryDelaySeconds</c>) to a time-based one. Remove the obsolete keys
    /// so the file matches the current shape; the new keys fall back to their defaults.
    /// </summary>
    private static void MigrateTo1(JsonObject root)
    {
        if (root["MailQueue"] is not JsonObject mq) return;
        mq.Remove("MaxRetries");
        mq.Remove("RetryDelaySeconds");
    }

    /// <summary>
    /// v1 → v2: additive only — <c>Certificate.FailClosed</c> (bool, default false) and
    /// <c>MailQueue.FailedEmailRetentionDays</c> (int, default 60) were introduced.
    /// Older files without the keys are already valid (the option binder falls back to
    /// the defaults), so there is nothing to transform; the version bump records which
    /// shape wrote the file.
    /// </summary>
    private static void MigrateTo2(JsonObject root)
    {
        // Intentionally empty: purely additive schema change.
        _ = root;
    }

    /// <summary>
    /// v2 → v3: additive only — <c>UpdateCheck.Enabled</c> (bool, default false) and
    /// <c>AdminNotifications.NotificationTypes.UpdateAvailable.Enabled</c> (bool, default
    /// false) were introduced for the opt-in weekly GitHub release check. Older files
    /// without the keys are already valid (the option binder falls back to the defaults),
    /// so there is nothing to transform; the version bump records which shape wrote the file.
    /// </summary>
    private static void MigrateTo3(JsonObject root)
    {
        // Intentionally empty: purely additive schema change.
        _ = root;
    }

    /// <summary>
    /// v3 → v4: additive only — <c>Telemetry.Enabled</c> (bool, default false) was introduced
    /// for the opt-in anonymous usage telemetry. Older files without the key are already valid
    /// (the option binder falls back to the default), so there is nothing to transform; the
    /// version bump records which shape wrote the file.
    /// </summary>
    private static void MigrateTo4(JsonObject root)
    {
        // Intentionally empty: purely additive schema change.
        _ = root;
    }

    /// <summary>
    /// v4 → v5: additive only — <c>Recommendations.Dismissed</c> (string array, default empty) was
    /// introduced so operators can permanently hide individual recommendation hints in the ConfigTool
    /// and in the periodic report. Older files without the key are already valid (the option binder
    /// falls back to an empty list, i.e. nothing is hidden), so there is nothing to transform; the
    /// version bump records which shape wrote the file.
    /// </summary>
    private static void MigrateTo5(JsonObject root)
    {
        // Intentionally empty: purely additive schema change.
        _ = root;
    }

    /// <summary>
    /// v5 → v6: <c>AdminNotifications.Enabled</c> becomes an authoritative setting instead of a value
    /// the ConfigTool derived from the recipient count on every save. The key already existed and
    /// already carried that derived value, so the only work is materialising it for files that never
    /// had it written (hand-edited configs): absent → <c>true</c> when recipients are configured.
    /// Without this an operator who adds a master switch to an existing install would find
    /// notifications silently off after the upgrade.
    /// </summary>
    private static void MigrateTo6(JsonObject root)
    {
        if (root["AdminNotifications"] is not JsonObject notifications) return;
        if (notifications["Enabled"] is not null) return;

        var recipients = notifications["RecipientAddresses"] as JsonArray;
        notifications["Enabled"] = recipients is { Count: > 0 };
    }

    /// <summary>
    /// v6 → v7: additive only — <c>AdminNotifications.NotificationTypes.GraphCertificateExpiringWarning</c>
    /// (bool, default true) was introduced for the advance warning before the Graph client certificate
    /// expires. Older files without the key are already valid (the option binder falls back to the
    /// default, i.e. the warning is on), so there is nothing to transform; the version bump records
    /// which shape wrote the file.
    /// </summary>
    private static void MigrateTo7(JsonObject root)
    {
        // Intentionally empty: purely additive schema change.
        _ = root;
    }
}

/// <summary>Outcome of a <see cref="ConfigMigrator.MigrateFile"/> call.</summary>
internal readonly record struct ConfigMigrationResult(
    bool Migrated, int From, int To, string? BackupPath, bool Incompatible);

/// <summary>
/// Applies <see cref="ConfigSchema"/> migrations to <c>graphmailer.json</c> on disk: backs up the
/// original (timestamped copy under <c>config\backups\</c>), migrates, and writes it back atomically.
/// Safe to call on every startup — a no-op when the file is already current.
/// </summary>
internal static class ConfigMigrator
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>How many schema-migration backups are kept in config\backups\.</summary>
    private const int MaxMigrationBackups = 10;

    /// <summary>
    /// Detects a syntactically corrupt <c>graphmailer.json</c> and moves it aside
    /// (<c>graphmailer.json.corrupt-&lt;timestamp&gt;</c>) so the service can start with
    /// built-in defaults instead of failing at configuration load on every start —
    /// a truncated hand-edit would otherwise keep the service down after each reboot.
    /// Returns the quarantine path, or null when the file is absent or parseable.
    /// </summary>
    public static string? QuarantineIfCorrupt(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            _ = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject
                ?? throw new JsonException("Root element is not a JSON object");
            return null;
        }
        catch (JsonException)
        {
            var quarantine = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            try
            {
                File.Move(path, quarantine, overwrite: true);
                return quarantine;
            }
            catch
            {
                return null;   // cannot move — startup will fail at config load as before
            }
        }
    }

    /// <summary>Migrates the file if its schema version is below <see cref="ConfigSchema.Current"/>.</summary>
    public static ConfigMigrationResult MigrateFile(string path, ILogger? logger = null)
    {
        if (!File.Exists(path))
            return new(false, ConfigSchema.Current, ConfigSchema.Current, null, false);

        JsonObject root;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) is not JsonObject obj)
                return new(false, 0, ConfigSchema.Current, null, false); // not a JSON object — leave it; ConfigService surfaces the error
            root = obj;
        }
        catch (JsonException)
        {
            return new(false, 0, ConfigSchema.Current, null, false); // invalid JSON — don't touch it
        }

        var from = ConfigSchema.ReadVersion(root);

        if (from > ConfigSchema.Current)
        {
            logger?.LogError(
                "[Config] graphmailer.json schema v{Found} is newer than this build (v{Supported}). " +
                "The file is used as-is; settings added by a newer version are ignored. Upgrade the application.",
                from, ConfigSchema.Current);
            return new(false, from, ConfigSchema.Current, null, Incompatible: true);
        }

        if (from == ConfigSchema.Current)
            return new(false, from, ConfigSchema.Current, null, false);

        // Back up the original before rewriting it.
        string backup;
        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(path) ?? ".", "backups");
            Directory.CreateDirectory(dir);
            backup = Path.Combine(dir, $"{Path.GetFileName(path)}.v{from}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.bak");
            File.Copy(path, backup, overwrite: true);
            PruneMigrationBackups(dir);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "[Config] Could not back up graphmailer.json before migration — aborting migration");
            return new(false, from, ConfigSchema.Current, null, false);
        }

        ConfigSchema.Migrate(root);

        var tmp = path + ".migrate.tmp";
        try
        {
            File.WriteAllText(tmp, root.ToJsonString(WriteOptions), Encoding.UTF8);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }

        logger?.LogInformation(
            "[Config] Migrated graphmailer.json schema v{From} → v{To} (backup: {Backup})",
            from, ConfigSchema.Current, backup);

        return new(true, from, ConfigSchema.Current, backup, false);
    }

    /// <summary>Keeps only the newest <see cref="MaxMigrationBackups"/> .bak files — migration backups must not accumulate forever.</summary>
    internal static void PruneMigrationBackups(string dir)
    {
        try
        {
            foreach (var old in Directory.GetFiles(dir, "*.bak")
                         .OrderByDescending(File.GetCreationTimeUtc)
                         .Skip(MaxMigrationBackups))
                File.Delete(old);
        }
        catch
        {
            // best-effort — a failed prune must never block a migration
        }
    }
}
