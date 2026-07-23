using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;

namespace GraphMailer.Service.Infrastructure.Config;

/// <summary>
/// Reads and writes <c>config\graphmailer.json</c>.
/// <list type="bullet">
///   <item>Missing file → all-defaults <see cref="ConfigDocument"/> (no error).</item>
///   <item>Missing section or field → that section's defaults applied.</item>
///   <item>Unknown top-level keys → preserved on save (round-trip safe).</item>
///   <item>Unknown keys within known sections → also preserved (deep clone).</item>
///   <item><c>ENC[…]</c> values → decrypted on <see cref="Load"/>, re-encrypted on <see cref="Save"/>.</item>
///   <item>Plaintext sensitive values → accepted (initial setup), encrypted on next save.</item>
///   <item>Writes atomically via temp file + <see cref="File.Move"/>.</item>
/// </list>
/// </summary>
internal sealed class ConfigService
{
    private const string EncPrefix = "ENC[";
    private const string EncSuffix = "]";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly IDataProtector _protector;
    private readonly string _defaultsPath;

    // Collects the paths of undecryptable ENC[...] values seen during a single Load().
    // Reset at the start of each Load; not used concurrently (UI thread / one instance).
    private List<string> _decryptFailures = [];

    /// <param name="defaultsPath">
    /// Path to the bundled <c>appsettings.json</c> whose values seed the defaults for any field
    /// the user's <c>graphmailer.json</c> omits — the single source of truth for scalar/object
    /// defaults, shared with the service. Defaults to <c>appsettings.json</c> next to the
    /// executable. Overridable for tests.
    /// </param>
    internal ConfigService(string filePath, IDataProtector protector, string? defaultsPath = null)
    {
        _filePath = filePath;
        _protector = protector;
        _defaultsPath = defaultsPath ?? Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    }

    public bool FileExists => File.Exists(_filePath);

    // ── Load ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads and decrypts <c>graphmailer.json</c>.
    /// Returns an all-defaults <see cref="ConfigDocument"/> when the file does not exist.
    /// </summary>
    /// <exception cref="ConfigLoadException">
    ///   The file exists but is empty, contains invalid JSON, or has a non-object root.
    /// </exception>
    /// <remarks>
    ///   An <c>ENC[…]</c> value that cannot be decrypted does <b>not</b> abort the load:
    ///   that single field is left blank and its path is reported via
    ///   <see cref="ConfigDocument.DecryptionFailures"/>, so the rest of the configuration
    ///   still loads.
    /// </remarks>
    public ConfigDocument Load()
    {
        if (!File.Exists(_filePath))
            return new ConfigDocument();

        string json;
        try
        {
            json = File.ReadAllText(_filePath, System.Text.Encoding.UTF8);
        }
        catch (IOException ex)
        {
            throw new ConfigLoadException($"Cannot read config file '{_filePath}': {ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(json))
            throw new ConfigLoadException($"Config file is empty: '{_filePath}'");

        return LoadFromJson(json);
    }

    /// <summary>
    /// Parses and decrypts a configuration JSON string (same semantics as <see cref="Load"/>,
    /// but from memory). Used by restore, which writes the decrypted document back out via
    /// <see cref="Save"/> to re-encrypt secrets with the local key.
    /// </summary>
    /// <exception cref="ConfigLoadException">The JSON is empty or not a JSON object.</exception>
    public ConfigDocument LoadFromJson(string json)
    {
        _decryptFailures = [];

        JsonObject root;
        try
        {
            var node = JsonNode.Parse(json);
            if (node is not JsonObject obj)
                throw new ConfigLoadException("Configuration must contain a JSON object at the root.");
            root = obj;
        }
        catch (JsonException ex)
        {
            throw new ConfigLoadException($"Invalid JSON in configuration: {ex.Message}", ex);
        }

        // Overlay the user document on top of the bundled appsettings.json defaults so that any
        // field the user omitted is read from the single shared default source instead of a
        // hard-coded literal. RawSource stays the *unmerged* user document so Save() round-trips
        // without materialising defaults.
        var merged = MergeWithDefaults(root);

        return new ConfigDocument
        {
            RawSource = root,
            SchemaVersion = ConfigSchema.ReadVersion(root),
            GraphApi = ReadGraphApi(merged),
            Smtp = ReadSmtp(merged),
            Certificate = ReadCertificate(merged),
            MailQueue = ReadMailQueue(merged),
            Access = ReadAccess(merged),
            Servers = ReadServers(merged),
            IpBlocking = ReadIpBlocking(merged),
            Monitoring = ReadMonitoring(merged),
            Metrics = ReadMetrics(merged),
            Notification = ReadNotifications(merged, root),
            Ndr = ReadNdr(merged),
            SenderValidation = ReadSenderValidation(merged),
            Logging = ReadLogging(merged),
            Backup = ReadBackup(merged),
            Recommendations = ReadRecommendations(merged),
            DecryptionFailures = [.. _decryptFailures],
        };
    }

    // ── Defaults overlay (single source of truth: bundled appsettings.json) ─────

    /// <summary>
    /// Returns <paramref name="user"/> overlaid on the bundled <c>appsettings.json</c> defaults.
    /// When the defaults file is missing/unreadable, the user document is used unchanged.
    /// </summary>
    private JsonObject MergeWithDefaults(JsonObject user)
        => LoadDefaultsRoot() is { } defaults ? MergeDefaults(defaults, user) : user;

    private JsonObject? LoadDefaultsRoot()
    {
        try
        {
            return File.Exists(_defaultsPath)
                ? JsonNode.Parse(File.ReadAllText(_defaultsPath)) as JsonObject
                : null;
        }
        catch { return null; } // malformed/unreadable appsettings.json → no overlay
    }

    /// <summary>
    /// Deep-merges <paramref name="user"/> onto a clone of <paramref name="defaults"/>:
    /// objects merge recursively; scalars and arrays are replaced wholesale by the user value
    /// when present (so a user-defined array — including an empty one — overrides the default,
    /// never index-merges with it).
    /// </summary>
    internal static JsonObject MergeDefaults(JsonObject defaults, JsonObject user)
    {
        var result = (JsonObject)defaults.DeepClone();
        foreach (var (key, value) in user)
        {
            result[key] = result[key] is JsonObject baseObj && value is JsonObject overObj
                ? MergeDefaults(baseObj, overObj)
                : value?.DeepClone();
        }
        return result;
    }

    /// <summary>
    /// Reads the on-disk config and returns its JSON with every <c>ENC[…]</c> value replaced
    /// by its decrypted plaintext (undecryptable values become empty). Used to build a
    /// portable backup: the secrets travel in plaintext inside the password-encrypted
    /// container and are re-encrypted with the target machine's key on restore.
    /// </summary>
    /// <exception cref="ConfigLoadException">The file is missing, empty, or not a JSON object.</exception>
    public string ReadDecryptedJson()
    {
        if (!File.Exists(_filePath))
            throw new ConfigLoadException($"Config file does not exist: '{_filePath}'");

        var json = File.ReadAllText(_filePath, System.Text.Encoding.UTF8);
        if (JsonNode.Parse(json) is not JsonObject root)
            throw new ConfigLoadException($"Config file must contain a JSON object at the root: '{_filePath}'");

        DecryptInline(root);
        return root.ToJsonString(WriteOptions);
    }

    private void DecryptInline(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                    if (TryDecryptEnc(value) is { } plain) obj[key] = plain;
                    else if (value is not null) DecryptInline(value);
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                    if (TryDecryptEnc(arr[i]) is { } plain) arr[i] = plain;
                    else if (arr[i] is { } child) DecryptInline(child);
                break;
        }
    }

    /// <summary>Returns the decrypted plaintext node for an <c>ENC[…]</c> value, else null.</summary>
    private JsonNode? TryDecryptEnc(JsonNode? node)
    {
        if (node is not JsonValue v || !v.TryGetValue<string>(out var s)
            || !s.StartsWith(EncPrefix, StringComparison.Ordinal)
            || !s.EndsWith(EncSuffix, StringComparison.Ordinal))
            return null;

        var cipher = s[EncPrefix.Length..^EncSuffix.Length];
        try { return JsonValue.Create(_protector.Unprotect(cipher)); }
        catch (CryptographicException) { return JsonValue.Create(string.Empty); }
    }

    // ── Save ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Serializes <paramref name="doc"/> to <c>graphmailer.json</c>.
    /// Sensitive fields are encrypted as <c>ENC[…]</c>.
    /// Unknown keys from <see cref="ConfigDocument.RawSource"/> are preserved.
    /// Writes atomically: a temp file is written first, then renamed over the target.
    /// </summary>
    public void Save(ConfigDocument doc)
    {
        // Start from the original document so any unknown keys survive the round-trip.
        var root = doc.RawSource?.DeepClone() as JsonObject ?? new JsonObject();

        // Stamp the current schema version on every save.
        root[ConfigSchema.VersionKey] = ConfigSchema.Current;

        WriteGraphApi(root, doc.GraphApi);
        WriteSmtp(root, doc.Smtp);
        WriteCertificate(root, doc.Certificate);
        WriteMailQueue(root, doc.MailQueue);
        WriteAccess(root, doc.Access);
        WriteServers(root, doc.Servers);
        WriteIpBlocking(root, doc.IpBlocking);
        WriteMonitoring(root, doc.Monitoring);
        WriteMetrics(root, doc.Metrics);
        WriteNotifications(root, doc.Notification);
        WriteNdr(root, doc.Ndr);
        WriteSenderValidation(root, doc.SenderValidation);
        WriteLogging(root, doc.Logging);
        WriteBackup(root, doc.Backup);
        WriteRecommendations(root, doc.Recommendations);

        WriteAtomically(root);
    }

    /// <summary>
    /// Persists only the dismissed-recommendation ids, leaving every other key on disk untouched.
    ///
    /// The ConfigTool calls this the moment a hint is dismissed or restored. A full
    /// <see cref="Save"/> would also write whatever the user has half-edited on other pages, and
    /// would raise the "unsaved changes" badge for what is really just a display preference — so
    /// this re-reads the file, replaces the one key and writes it back atomically.
    ///
    /// No-op when the file does not exist yet: the caller keeps the ids in its
    /// <see cref="ConfigDocument"/>, and the first real <see cref="Save"/> persists them.
    /// </summary>
    public void UpdateDismissedRecommendations(IReadOnlyList<string> ids)
    {
        if (!File.Exists(_filePath)) return;

        var json = File.ReadAllText(_filePath, System.Text.Encoding.UTF8);
        if (JsonNode.Parse(json) is not JsonObject root)
            throw new ConfigLoadException($"Config file must contain a JSON object at the root: '{_filePath}'");

        WriteRecommendations(root, new ConfigDocument.RecommendationsSection { Dismissed = [.. ids] });
        WriteAtomically(root);
    }

    /// <summary>Writes <paramref name="root"/> to the config path via temp file + rename.</summary>
    private void WriteAtomically(JsonObject root)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var temp = _filePath + ".tmp";
        try
        {
            File.WriteAllText(temp, root.ToJsonString(WriteOptions), System.Text.Encoding.UTF8);
            File.Move(temp, _filePath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best-effort cleanup */ }
            throw;
        }
    }

    // ── Section readers ───────────────────────────────────────────────────────

    private ConfigDocument.GraphApiSection ReadGraphApi(JsonObject root)
    {
        var s = root["GraphApi"] as JsonObject ?? new JsonObject();
        return new ConfigDocument.GraphApiSection
        {
            TenantId = Str(s, "TenantId"),
            ClientId = Str(s, "ClientId"),
            ClientSecret = Decrypt(s, "ClientSecret", "GraphApi.ClientSecret"),
            ClientCertificateThumbprint = Str(s, "ClientCertificateThumbprint"),
            ClientCertificateSubjectName = Str(s, "ClientCertificateSubjectName"),
            ClientCertificateIssuer = Str(s, "ClientCertificateIssuer"),
        };
    }

    private static ConfigDocument.SmtpSection ReadSmtp(JsonObject root)
    {
        var s = root["Smtp"] as JsonObject ?? new JsonObject();
        return new ConfigDocument.SmtpSection
        {
            MaxSizeBytes = s["MaxSizeBytes"]?.GetValue<long>() ?? 26_214_400,
            Banner = Str(s, "Banner") ?? "GraphMailer",
        };
    }

    private static ConfigDocument.CertSection ReadCertificate(JsonObject root)
    {
        var s = root["Certificate"] as JsonObject ?? new JsonObject();
        return new ConfigDocument.CertSection
        {
            StoreLocation = Str(s, "StoreLocation") ?? "LocalMachine",
            StoreName = Str(s, "StoreName") ?? "My",
            SubjectName = Str(s, "SubjectName"),
            Issuer = Str(s, "Issuer"),
            FailClosed = s["FailClosed"]?.GetValue<bool>() ?? false,
        };
    }

    private static ConfigDocument.MailQueueSection ReadMailQueue(JsonObject root)
    {
        var s = root["MailQueue"] as JsonObject ?? new JsonObject();
        return new ConfigDocument.MailQueueSection
        {
            MailDir = Str(s, "MailDir") ?? string.Empty,
            PollingIntervalSeconds = s["PollingIntervalSeconds"]?.GetValue<int>() ?? 5,
            TransientRetryCount = s["TransientRetryCount"]?.GetValue<int>() ?? 6,
            TransientRetryIntervalSeconds = s["TransientRetryIntervalSeconds"]?.GetValue<int>() ?? 300,
            RetryIntervalSeconds = s["RetryIntervalSeconds"]?.GetValue<int>() ?? 900,
            MessageExpirationHours = s["MessageExpirationHours"]?.GetValue<int>() ?? 24,
            BatchSize = s["BatchSize"]?.GetValue<int>() ?? 10,
            ArchiveSentEmails = s["ArchiveSentEmails"]?.GetValue<bool>() ?? false,
            SentEmailRetentionDays = s["SentEmailRetentionDays"]?.GetValue<int>() ?? 7,
            FailedEmailRetentionDays = s["FailedEmailRetentionDays"]?.GetValue<int>() ?? 60,
        };
    }

    private ConfigDocument.AccessSection ReadAccess(JsonObject root)
    {
        return new ConfigDocument.AccessSection
        {
            IpWhitelist = ReadStringArray(root, "IpWhitelist"),
            IpWhitelistComments = ReadStringDict(root, "IpWhitelistComments"),
            IpBlacklist = ReadStringArray(root, "IpBlacklist"),
            IpBlacklistComments = ReadStringDict(root, "IpBlacklistComments"),
            AllowedSenders = ReadStringArray(root, "AllowedSenders"),
            BlockedSenders = ReadStringArray(root, "BlockedSenders"),
            AllowedRecipients = ReadStringArray(root, "AllowedRecipients"),
            BlockedRecipients = ReadStringArray(root, "BlockedRecipients"),
            Users = ReadUsers(root),
        };
    }

    private List<ConfigDocument.UserEntry> ReadUsers(JsonObject root)
    {
        if (root["Users"] is not JsonArray arr) return [];
        var list = new List<ConfigDocument.UserEntry>(arr.Count);
        for (int i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not JsonObject u) continue;
            list.Add(new ConfigDocument.UserEntry
            {
                Enabled = u["Enabled"]?.GetValue<bool>() ?? true,
                Username = Str(u, "Username") ?? string.Empty,
                DisplayName = Str(u, "DisplayName") ?? string.Empty,
                Password = Decrypt(u, "Password", $"Users[{i}].Password"),
                CaptureNextPassword = u["CaptureNextPassword"]?.GetValue<bool>() ?? false,
                FromRestrictions = ReadStringArray(u, "FromRestrictions"),
            });
        }
        return list;
    }

    private static List<ConfigDocument.ServerEntry> ReadServers(JsonObject root)
    {
        if (root["Servers"] is not JsonArray arr) return [];
        var list = new List<ConfigDocument.ServerEntry>(arr.Count);
        foreach (var item in arr)
        {
            if (item is not JsonObject s) continue;
            list.Add(new ConfigDocument.ServerEntry
            {
                Enabled = s["Enabled"]?.GetValue<bool>() ?? true,
                Name = Str(s, "Name") ?? "SMTP",
                Port = s["Port"]?.GetValue<int>() ?? 2525,
                Mode = Str(s, "Mode") ?? "Plain",
                AuthMode = Str(s, "AuthMode") ?? (s["AuthRequired"]?.GetValue<bool>() == true ? "Required" : "Optional"),
            });
        }
        return list;
    }

    // ── Section writers ───────────────────────────────────────────────────────

    private void WriteGraphApi(JsonObject root, ConfigDocument.GraphApiSection s)
    {
        var o = EnsureSection(root, "GraphApi");
        o["TenantId"] = s.TenantId;
        o["ClientId"] = s.ClientId;
        o["ClientSecret"] = Encrypt(s.ClientSecret);
        o["ClientCertificateThumbprint"] = s.ClientCertificateThumbprint;
        o["ClientCertificateSubjectName"] = s.ClientCertificateSubjectName;
        o["ClientCertificateIssuer"] = s.ClientCertificateIssuer;
    }

    private static void WriteSmtp(JsonObject root, ConfigDocument.SmtpSection s)
    {
        var o = EnsureSection(root, "Smtp");
        o["MaxSizeBytes"] = s.MaxSizeBytes;
        o["Banner"] = s.Banner;
    }

    private static void WriteCertificate(JsonObject root, ConfigDocument.CertSection s)
    {
        var o = EnsureSection(root, "Certificate");
        o["StoreLocation"] = s.StoreLocation;
        o["StoreName"] = s.StoreName;
        o["SubjectName"] = s.SubjectName;
        o["Issuer"] = s.Issuer;
        o["FailClosed"] = s.FailClosed;
    }

    private static void WriteMailQueue(JsonObject root, ConfigDocument.MailQueueSection s)
    {
        var o = EnsureSection(root, "MailQueue");
        if (!string.IsNullOrEmpty(s.MailDir))
            o["MailDir"] = s.MailDir;
        else
            o.Remove("MailDir");
        o["PollingIntervalSeconds"] = s.PollingIntervalSeconds;
        o["TransientRetryCount"] = s.TransientRetryCount;
        o["TransientRetryIntervalSeconds"] = s.TransientRetryIntervalSeconds;
        o["RetryIntervalSeconds"] = s.RetryIntervalSeconds;
        o["MessageExpirationHours"] = s.MessageExpirationHours;
        o["BatchSize"] = s.BatchSize;
        o["ArchiveSentEmails"] = s.ArchiveSentEmails;
        o["SentEmailRetentionDays"] = s.SentEmailRetentionDays;
        o["FailedEmailRetentionDays"] = s.FailedEmailRetentionDays;
    }

    private void WriteAccess(JsonObject root, ConfigDocument.AccessSection s)
    {
        root["IpWhitelist"] = ToJsonArray(s.IpWhitelist);
        root["IpWhitelistComments"] = ToJsonDict(s.IpWhitelistComments);
        root["IpBlacklist"] = ToJsonArray(s.IpBlacklist);
        root["IpBlacklistComments"] = ToJsonDict(s.IpBlacklistComments);
        root["AllowedSenders"] = ToJsonArray(s.AllowedSenders);
        root["BlockedSenders"] = ToJsonArray(s.BlockedSenders);
        root["AllowedRecipients"] = ToJsonArray(s.AllowedRecipients);
        root["BlockedRecipients"] = ToJsonArray(s.BlockedRecipients);

        var usersArr = new JsonArray();
        foreach (var u in s.Users)
        {
            usersArr.Add(new JsonObject
            {
                ["Enabled"] = u.Enabled,
                ["Username"] = u.Username,
                ["DisplayName"] = u.DisplayName,
                ["Password"] = Encrypt(u.Password),
                ["CaptureNextPassword"] = u.CaptureNextPassword,
                ["FromRestrictions"] = ToJsonArray(u.FromRestrictions),
            });
        }
        root["Users"] = usersArr;
    }

    private static void WriteServers(JsonObject root, List<ConfigDocument.ServerEntry> servers)
    {
        var arr = new JsonArray();
        foreach (var s in servers)
        {
            arr.Add(new JsonObject
            {
                ["Enabled"] = s.Enabled,
                ["Name"] = s.Name,
                ["Port"] = s.Port,
                ["Mode"] = s.Mode,
                ["AuthMode"] = s.AuthMode,
                ["AuthRequired"] = s.AuthMode == "Required",
            });
        }
        root["Servers"] = arr;
    }

    private static ConfigDocument.MonitoringSection ReadMonitoring(JsonObject root)
    {
        var cert = root["CertificateMonitoring"] as JsonObject ?? new JsonObject();
        var disk = root["DiskSpaceMonitoring"] as JsonObject ?? new JsonObject();
        var port = root["PortMonitoring"] as JsonObject ?? new JsonObject();
        var graph = root["GraphApiMonitoring"] as JsonObject ?? new JsonObject();
        var update = root["UpdateCheck"] as JsonObject ?? new JsonObject();
        var telemetry = root["Telemetry"] as JsonObject ?? new JsonObject();
        return new ConfigDocument.MonitoringSection
        {
            CertWarnDays = cert["WarningThresholdDays"]?.GetValue<int>() ?? 14,
            DiskWarnPct = disk["ThresholdPercent"]?.GetValue<int>() ?? 10,
            PortCheckIntervalMinutes = port["CheckIntervalMinutes"]?.GetValue<int>() ?? 5,
            GraphCheckIntervalMinutes = graph["CheckIntervalMinutes"]?.GetValue<int>() ?? 15,
            UpdateCheckEnabled = update["Enabled"]?.GetValue<bool>() ?? false,
            TelemetryEnabled = telemetry["Enabled"]?.GetValue<bool>() ?? false,
        };
    }

    /// <param name="userRoot">
    /// The <b>unmerged</b> user document. Needed for <c>Enabled</c>: the defaults overlay always
    /// supplies it from appsettings.json, which makes "absent in the user file" indistinguishable
    /// from "explicitly false" on the merged document.
    /// </param>
    private static ConfigDocument.NotificationSection ReadNotifications(JsonObject root, JsonObject userRoot)
    {
        var o = root["AdminNotifications"] as JsonObject ?? new JsonObject();
        var userEnabled = (userRoot["AdminNotifications"] as JsonObject)?["Enabled"]?.GetValue<bool>();
        var types = o["NotificationTypes"] as JsonObject ?? new JsonObject();
        var report = o["ScheduledReport"] as JsonObject ?? new JsonObject();
        var recipients = o["RecipientAddresses"] as JsonArray;
        var recipientList = recipients?.Select(n => n?.GetValue<string>()).OfType<string>().ToList() ?? [];
        return new ConfigDocument.NotificationSection
        {
            // Before schema v6 the ConfigTool derived this key from the recipient count instead of
            // exposing it. A file that predates the migration (e.g. a restored backup) is read with
            // that same rule, so notifications never silently switch themselves off.
            NotifEnabled = userEnabled ?? recipientList.Count > 0,
            RecipientAddresses = recipientList,
            NotifFrom = Str(o, "SenderAddress"),
            SubjectPrefix = Str(o, "SubjectPrefix") ?? "[GraphMailer]",
            NotifIpBlocked = GetTypeEnabled(types, "IpBlockedAlert", true),
            NotifDeliveryFailed = GetTypeEnabled(types, "EmailDeliveryFailed", true),
            NotifCertExpiring = GetTypeEnabled(types, "CertificateExpiringWarning", true),
            NotifCertExpired = GetTypeEnabled(types, "CertificateExpired", true),
            NotifGraphCertExpiring = GetTypeEnabled(types, "GraphCertificateExpiringWarning", true),
            NotifDiskSpace = GetTypeEnabled(types, "LowDiskSpaceWarning", true),
            NotifGraphDown = GetTypeEnabled(types, "GraphApiConnectionError", true),
            NotifPortDown = GetTypeEnabled(types, "PortMonitoringAlert", true),
            NotifServiceStartStop = GetTypeEnabled(types, "ServiceStartStopAlert", false),
            NotifBackup = GetTypeEnabled(types, "BackupResult", true),
            NotifUpdateAvailable = GetTypeEnabled(types, "UpdateAvailable", false),
            ReportEnabled = report["Enabled"]?.GetValue<bool>() ?? false,
            ReportFrequency = Str(report, "Frequency") ?? "Weekly",
            ReportTimeOfDay = Str(report, "TimeOfDay") ?? "07:00",
            ReportDayOfWeek = Str(report, "DayOfWeek") ?? "Monday",
            ReportDayOfMonth = report["DayOfMonth"]?.GetValue<int>() ?? 1,
        };
    }

    private static bool GetTypeEnabled(JsonObject types, string key, bool defaultValue)
        => (types[key] as JsonObject)?["Enabled"]?.GetValue<bool>() ?? defaultValue;

    private static ConfigDocument.IpBlockingSection ReadIpBlocking(JsonObject root)
    {
        var s = root["IpBlockingProtection"] as JsonObject ?? new JsonObject();
        return new ConfigDocument.IpBlockingSection
        {
            FailureThreshold = s["FailureThreshold"]?.GetValue<int>() ?? 10,
            TimeframeSeconds = s["TimeframeSeconds"]?.GetValue<int>() ?? 600,
            BlockDurationSeconds = s["BlockDurationSeconds"]?.GetValue<int>() ?? 600,
        };
    }

    private static void WriteMonitoring(JsonObject root, ConfigDocument.MonitoringSection s)
    {
        EnsureSection(root, "CertificateMonitoring")["WarningThresholdDays"] = s.CertWarnDays;
        EnsureSection(root, "DiskSpaceMonitoring")["ThresholdPercent"] = s.DiskWarnPct;
        EnsureSection(root, "PortMonitoring")["CheckIntervalMinutes"] = s.PortCheckIntervalMinutes;
        EnsureSection(root, "GraphApiMonitoring")["CheckIntervalMinutes"] = s.GraphCheckIntervalMinutes;
        EnsureSection(root, "UpdateCheck")["Enabled"] = s.UpdateCheckEnabled;
        EnsureSection(root, "Telemetry")["Enabled"] = s.TelemetryEnabled;
    }

    private static void WriteNotifications(JsonObject root, ConfigDocument.NotificationSection s)
    {
        var o = EnsureSection(root, "AdminNotifications");
        o["Enabled"] = s.NotifEnabled;
        o["SenderAddress"] = string.IsNullOrWhiteSpace(s.NotifFrom) ? null : (JsonNode)s.NotifFrom!;
        o["SubjectPrefix"] = s.SubjectPrefix;

        var arr = new JsonArray();
        foreach (var addr in s.RecipientAddresses) arr.Add(addr);
        o["RecipientAddresses"] = arr;

        var types = EnsureSection(o, "NotificationTypes");
        SetTypeEnabled(types, "IpBlockedAlert", s.NotifIpBlocked);
        SetTypeEnabled(types, "EmailDeliveryFailed", s.NotifDeliveryFailed);
        SetTypeEnabled(types, "CertificateExpiringWarning", s.NotifCertExpiring);
        SetTypeEnabled(types, "CertificateExpired", s.NotifCertExpired);
        SetTypeEnabled(types, "GraphCertificateExpiringWarning", s.NotifGraphCertExpiring);
        SetTypeEnabled(types, "LowDiskSpaceWarning", s.NotifDiskSpace);
        SetTypeEnabled(types, "GraphApiConnectionError", s.NotifGraphDown);
        SetTypeEnabled(types, "PortMonitoringAlert", s.NotifPortDown);
        SetTypeEnabled(types, "ServiceStartStopAlert", s.NotifServiceStartStop);
        SetTypeEnabled(types, "BackupResult", s.NotifBackup);
        SetTypeEnabled(types, "UpdateAvailable", s.NotifUpdateAvailable);

        var report = EnsureSection(o, "ScheduledReport");
        report["Enabled"] = s.ReportEnabled;
        report["Frequency"] = s.ReportFrequency;
        report["TimeOfDay"] = s.ReportTimeOfDay;
        report["DayOfWeek"] = s.ReportDayOfWeek;
        report["DayOfMonth"] = s.ReportDayOfMonth;
    }

    private static void SetTypeEnabled(JsonObject types, string key, bool enabled)
        => EnsureSection(types, key)["Enabled"] = enabled;

    private static ConfigDocument.NdrSection ReadNdr(JsonObject root)
    {
        var o = root["NdrNotifications"] as JsonObject ?? new JsonObject();
        return new ConfigDocument.NdrSection
        {
            NdrEnabled = o["Enabled"]?.GetValue<bool>() ?? false,
            NdrNotifySender = o["NotifySender"]?.GetValue<bool>() ?? true,
            NdrNotifyAdmin = o["NotifyAdmin"]?.GetValue<bool>() ?? false,
        };
    }

    private static void WriteNdr(JsonObject root, ConfigDocument.NdrSection s)
    {
        var o = EnsureSection(root, "NdrNotifications");
        o["Enabled"] = s.NdrEnabled;
        o["NotifySender"] = s.NdrNotifySender;
        o["NotifyAdmin"] = s.NdrNotifyAdmin;
    }

    private static ConfigDocument.SenderValidationSection ReadSenderValidation(JsonObject root)
    {
        var o = root["SenderValidation"] as JsonObject ?? new JsonObject();
        return new ConfigDocument.SenderValidationSection
        {
            SvEnabled = o["Enabled"]?.GetValue<bool>() ?? false,
            SvRefreshIntervalMinutes = o["RefreshIntervalMinutes"]?.GetValue<int>() ?? 60,
            SvFailClosed = o["FailClosed"]?.GetValue<bool>() ?? false,
        };
    }

    private static void WriteSenderValidation(JsonObject root, ConfigDocument.SenderValidationSection s)
    {
        var o = EnsureSection(root, "SenderValidation");
        o["Enabled"] = s.SvEnabled;
        o["RefreshIntervalMinutes"] = s.SvRefreshIntervalMinutes;
        o["FailClosed"] = s.SvFailClosed;
    }

    private ConfigDocument.BackupSection ReadBackup(JsonObject root)
    {
        var o = root["Backup"] as JsonObject ?? new JsonObject();
        var email = o["Email"] as JsonObject ?? new JsonObject();
        return new ConfigDocument.BackupSection
        {
            BackupEnabled = o["Enabled"]?.GetValue<bool>() ?? false,
            // Last-resort fallback only — the real default comes from appsettings.json via the
            // defaults overlay. Kept in sync with appsettings.json / ConfigDocument ("Weekly").
            Frequency = Str(o, "Frequency") ?? "Weekly",
            TimeOfDay = Str(o, "TimeOfDay") ?? "03:00",
            DayOfWeek = Str(o, "DayOfWeek") ?? "Sunday",
            MaxBackups = o["MaxBackups"]?.GetValue<int>() ?? 14,
            Directory = Str(o, "Directory"),
            Password = Decrypt(o, "Password", "Backup.Password"),
            EmailEnabled = email["Enabled"]?.GetValue<bool>() ?? false,
            EmailRecipients = ReadStringArray(email, "Recipients"),
        };
    }

    private void WriteBackup(JsonObject root, ConfigDocument.BackupSection s)
    {
        var o = EnsureSection(root, "Backup");
        o["Enabled"] = s.BackupEnabled;
        o["Frequency"] = s.Frequency;
        o["TimeOfDay"] = s.TimeOfDay;
        o["DayOfWeek"] = s.DayOfWeek;
        o["MaxBackups"] = s.MaxBackups;
        o["Directory"] = string.IsNullOrWhiteSpace(s.Directory) ? null : (JsonNode)s.Directory!;
        o["Password"] = Encrypt(s.Password);

        var email = EnsureSection(o, "Email");
        email["Enabled"] = s.EmailEnabled;
        email["Recipients"] = ToJsonArray(s.EmailRecipients);
    }

    private static ConfigDocument.LoggingSection ReadLogging(JsonObject root)
    {
        // Serilog allows both "MinimumLevel": "Debug" and "MinimumLevel": { "Default": "Debug" }
        var level = (root["Serilog"] as JsonObject)?["MinimumLevel"] switch
        {
            JsonObject o => o["Default"]?.GetValue<string>(),
            JsonValue v when v.TryGetValue<string>(out var str) => str,
            _ => null,
        };
        return new ConfigDocument.LoggingSection
        {
            DefaultLevel = level ?? "Information",
        };
    }

    private static void WriteLogging(JsonObject root, ConfigDocument.LoggingSection s)
    {
        // When no MinimumLevel is configured yet, don't materialize one just to store
        // the default — graphmailer.json is loaded after appsettings.json and the new
        // entry would permanently shadow a level configured there.
        bool hasExisting = (root["Serilog"] as JsonObject)?["MinimumLevel"] is not null;
        if (!hasExisting && s.DefaultLevel == "Information") return;

        var minLevel = EnsureSection(EnsureSection(root, "Serilog"), "MinimumLevel");
        minLevel["Default"] = s.DefaultLevel;
    }

    private static ConfigDocument.RecommendationsSection ReadRecommendations(JsonObject root)
    {
        var o = root["Recommendations"] as JsonObject ?? new JsonObject();
        return new ConfigDocument.RecommendationsSection
        {
            Dismissed = ReadStringArray(o, "Dismissed"),
        };
    }

    private static void WriteRecommendations(JsonObject root, ConfigDocument.RecommendationsSection s)
        => EnsureSection(root, "Recommendations")["Dismissed"] = ToJsonArray(s.Dismissed);

    private static void WriteIpBlocking(JsonObject root, ConfigDocument.IpBlockingSection s)
    {
        var o = EnsureSection(root, "IpBlockingProtection");
        o["FailureThreshold"] = s.FailureThreshold;
        o["TimeframeSeconds"] = s.TimeframeSeconds;
        o["BlockDurationSeconds"] = s.BlockDurationSeconds;
    }

    private static ConfigDocument.MetricsSection ReadMetrics(JsonObject root)
    {
        var o = root["Metrics"] as JsonObject ?? new JsonObject();
        var p = o["PerformanceMetrics"] as JsonObject ?? new JsonObject();
        return new ConfigDocument.MetricsSection
        {
            Enabled = o["Enabled"]?.GetValue<bool>() ?? true,
            RetentionDays = o["RetentionDays"]?.GetValue<int>() ?? 90,
            CleanupIntervalHours = o["CleanupIntervalHours"]?.GetValue<int>() ?? 24,
            PerfMetricsEnabled = p["Enabled"]?.GetValue<bool>() ?? true,
            PerfMemoryIntervalSeconds = p["MemoryIntervalSeconds"]?.GetValue<int>() ?? 60,
            PerfCpuIntervalSeconds = p["CpuIntervalSeconds"]?.GetValue<int>() ?? 60,
            PerfDiskIntervalSeconds = p["DiskIntervalSeconds"]?.GetValue<int>() ?? 300,
        };
    }

    private static void WriteMetrics(JsonObject root, ConfigDocument.MetricsSection s)
    {
        var o = EnsureSection(root, "Metrics");
        o["Enabled"] = s.Enabled;
        o["RetentionDays"] = s.RetentionDays;
        o["CleanupIntervalHours"] = s.CleanupIntervalHours;
        var p = EnsureSection(o, "PerformanceMetrics");
        p["Enabled"] = s.PerfMetricsEnabled;
        p["MemoryIntervalSeconds"] = s.PerfMemoryIntervalSeconds;
        p["CpuIntervalSeconds"] = s.PerfCpuIntervalSeconds;
        p["DiskIntervalSeconds"] = s.PerfDiskIntervalSeconds;
    }

    // ── Encryption helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Reads a field from <paramref name="obj"/> and decrypts it when it is in
    /// <c>ENC[…]</c> format. Plaintext values (initial setup) are returned as-is.
    /// </summary>
    private string? Decrypt(JsonObject obj, string key, string fieldPath)
    {
        var raw = Str(obj, key);
        if (raw is null) return null;

        if (raw.StartsWith(EncPrefix, StringComparison.Ordinal)
         && raw.EndsWith(EncSuffix, StringComparison.Ordinal))
        {
            var cipher = raw[EncPrefix.Length..^EncSuffix.Length];
            try
            {
                return _protector.Unprotect(cipher);
            }
            catch (CryptographicException)
            {
                // Don't abort the whole load for one bad secret: record the field and
                // return null so it loads blank. The caller warns via DecryptionFailures.
                _decryptFailures.Add(fieldPath);
                return null;
            }
        }

        return raw; // plaintext – acceptable during initial setup; encrypted on next Save
    }

    /// <summary>
    /// Encrypts <paramref name="value"/> as <c>ENC[…]</c>.
    /// Returns <see langword="null"/> when <paramref name="value"/> is null.
    /// </summary>
    private JsonNode? Encrypt(string? value)
    {
        if (value is null) return null;
        var cipher = _protector.Protect(value);
        return JsonValue.Create($"{EncPrefix}{cipher}{EncSuffix}");
    }

    // ── JSON helpers ──────────────────────────────────────────────────────────

    private static string? Str(JsonObject obj, string key)
        => obj[key]?.GetValue<string>();

    private static Dictionary<string, string> ReadStringDict(JsonObject root, string key)
    {
        var result = new Dictionary<string, string>();
        if (root[key] is not JsonObject obj) return result;
        foreach (var kvp in obj)
        {
            var v = kvp.Value?.GetValue<string>();
            if (v is not null) result[kvp.Key] = v;
        }
        return result;
    }

    private static JsonObject ToJsonDict(Dictionary<string, string> dict)
    {
        var obj = new JsonObject();
        foreach (var kvp in dict)
            obj[kvp.Key] = JsonValue.Create(kvp.Value);
        return obj;
    }

    private static List<string> ReadStringArray(JsonObject root, string key)
    {
        if (root[key] is not JsonArray arr) return [];
        var list = new List<string>(arr.Count);
        foreach (var item in arr)
        {
            var s = item?.GetValue<string>();
            if (s is not null) list.Add(s);
        }
        return list;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> items)
    {
        var arr = new JsonArray();
        foreach (var s in items)
            arr.Add(JsonValue.Create(s));
        return arr;
    }

    private static JsonObject EnsureSection(JsonObject root, string key)
    {
        if (root[key] is JsonObject existing) return existing;
        var o = new JsonObject();
        root[key] = o;
        return o;
    }
}
