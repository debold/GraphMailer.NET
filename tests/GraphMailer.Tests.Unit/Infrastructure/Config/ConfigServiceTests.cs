using System.Text.Json.Nodes;
using GraphMailer.Service.Infrastructure.Config;
using Microsoft.AspNetCore.DataProtection;

namespace GraphMailer.Tests.Unit.Infrastructure.Config;

/// <summary>
/// Unit tests for <see cref="ConfigService"/> covering:
///   - Load: missing file, complete JSON, partial sections, unknown keys
///   - Load: ENC[...] decryption, plaintext pass-through
///   - Load: invalid JSON, empty file, truncated JSON, non-object root
///   - Load: corrupt ENC[...] values with precise field-path reporting
///   - Save: JSON structure, sensitive-field encryption, null handling, atomic write
///   - Save: preservation of unknown top-level keys from RawSource
///   - Round-trip: Load → Save → Load identity for all sections
/// </summary>
public sealed class ConfigServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _filePath;
    private readonly IDataProtector _protector;
    private readonly ConfigService _sut;

    public ConfigServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"gm-cfg-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _filePath = Path.Combine(_dir, "graphmailer.json");
        _protector = new EphemeralDataProtectionProvider().CreateProtector("GraphMailer.Config");
        _sut = new ConfigService(_filePath, _protector);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // =========================================================================
    // Load – file not found
    // =========================================================================

    [Fact]
    public void Load_NoFile_ReturnsAllDefaults()
    {
        var doc = _sut.Load();

        doc.GraphApi.TenantId.Should().BeNull();
        doc.GraphApi.ClientId.Should().BeNull();
        doc.GraphApi.ClientSecret.Should().BeNull();
        doc.GraphApi.ClientCertificateThumbprint.Should().BeNull();
        doc.Smtp.MaxSizeBytes.Should().Be(26_214_400);
        doc.Smtp.Banner.Should().Be("GraphMailer");
        doc.Certificate.StoreLocation.Should().Be("LocalMachine");
        doc.Certificate.StoreName.Should().Be("My");
        doc.Certificate.SubjectName.Should().BeNull();
        doc.MailQueue.PollingIntervalSeconds.Should().Be(5);
        doc.MailQueue.TransientRetryCount.Should().Be(6);
        doc.MailQueue.TransientRetryIntervalSeconds.Should().Be(300);
        doc.MailQueue.RetryIntervalSeconds.Should().Be(900);
        doc.MailQueue.MessageExpirationHours.Should().Be(24);
        doc.MailQueue.BatchSize.Should().Be(10);
        doc.MailQueue.ArchiveSentEmails.Should().BeFalse();
        doc.MailQueue.SentEmailRetentionDays.Should().Be(7);
        doc.Access.IpWhitelist.Should().BeEmpty();
        doc.Access.IpBlacklist.Should().BeEmpty();
        doc.Access.Users.Should().BeEmpty();
        doc.Servers.Should().BeEmpty();
        doc.RawSource.Should().BeNull();
    }

    [Fact]
    public void Load_NoFile_FileExistsReturnsFalse()
        => _sut.FileExists.Should().BeFalse();

    // =========================================================================
    // Load – valid JSON
    // =========================================================================

    [Fact]
    public void Load_FullConfig_ReadsAllSections()
    {
        WriteJson("""
        {
            "GraphApi": {
                "TenantId": "tenant-abc",
                "ClientId": "client-xyz",
                "ClientCertificateThumbprint": "AABBCCDD"
            },
            "Smtp": { "MaxSizeBytes": 10485760, "Banner": "MyRelay" },
            "Certificate": { "SubjectName": "smtp.corp.com", "Issuer": "Corp CA" },
            "MailQueue": {
                "PollingIntervalSeconds": 10, "TransientRetryCount": 4,
                "TransientRetryIntervalSeconds": 120, "RetryIntervalSeconds": 600,
                "MessageExpirationHours": 48, "BatchSize": 20,
                "ArchiveSentEmails": true, "SentEmailRetentionDays": 14
            },
            "Servers": [
                { "Name": "Main", "Port": 587, "Mode": "StartTls", "AuthRequired": true }
            ],
            "IpWhitelist": ["192.168.1.0/24", "10.0.0.1"],
            "IpBlacklist": ["203.0.113.5"],
            "AllowedSenders": ["allowed@corp.com"],
            "BlockedRecipients": ["spam@example.com"],
            "Users": [
                { "Username": "alice", "Password": "secret", "FromRestrictions": ["alice@corp.com"] }
            ]
        }
        """);

        var doc = _sut.Load();

        doc.GraphApi.TenantId.Should().Be("tenant-abc");
        doc.GraphApi.ClientId.Should().Be("client-xyz");
        doc.GraphApi.ClientCertificateThumbprint.Should().Be("AABBCCDD");
        doc.Smtp.MaxSizeBytes.Should().Be(10_485_760);
        doc.Smtp.Banner.Should().Be("MyRelay");
        doc.Certificate.SubjectName.Should().Be("smtp.corp.com");
        doc.Certificate.Issuer.Should().Be("Corp CA");
        doc.MailQueue.PollingIntervalSeconds.Should().Be(10);
        doc.MailQueue.TransientRetryCount.Should().Be(4);
        doc.MailQueue.TransientRetryIntervalSeconds.Should().Be(120);
        doc.MailQueue.RetryIntervalSeconds.Should().Be(600);
        doc.MailQueue.MessageExpirationHours.Should().Be(48);
        doc.MailQueue.BatchSize.Should().Be(20);
        doc.MailQueue.ArchiveSentEmails.Should().BeTrue();
        doc.MailQueue.SentEmailRetentionDays.Should().Be(14);
        doc.Servers.Should().HaveCount(1);
        doc.Servers[0].Port.Should().Be(587);
        doc.Servers[0].Mode.Should().Be("StartTls");
        doc.Servers[0].AuthMode.Should().Be("Required"); // backward-compat: AuthRequired:true → AuthMode:"Required"
        doc.Access.IpWhitelist.Should().BeEquivalentTo(["192.168.1.0/24", "10.0.0.1"]);
        doc.Access.IpBlacklist.Should().ContainSingle("203.0.113.5");
        doc.Access.AllowedSenders.Should().ContainSingle("allowed@corp.com");
        doc.Access.BlockedRecipients.Should().ContainSingle("spam@example.com");
        doc.Access.Users.Should().HaveCount(1);
        doc.Access.Users[0].Username.Should().Be("alice");
        doc.Access.Users[0].Password.Should().Be("secret");
        doc.Access.Users[0].FromRestrictions.Should().ContainSingle("alice@corp.com");
    }

    [Fact]
    public void Load_MissingSection_ReturnsDefaultsForThatSection()
    {
        WriteJson("""{ "Smtp": { "Banner": "OnlySmtp" } }""");

        var doc = _sut.Load();

        doc.Smtp.Banner.Should().Be("OnlySmtp");
        // All other sections untouched → defaults
        doc.GraphApi.TenantId.Should().BeNull();
        doc.MailQueue.PollingIntervalSeconds.Should().Be(5);
        doc.Certificate.StoreLocation.Should().Be("LocalMachine");
        doc.Access.Users.Should().BeEmpty();
        doc.Servers.Should().BeEmpty();
    }

    [Fact]
    public void Load_PartialSection_MissingFieldsGetDefaults()
    {
        WriteJson("""
        {
            "MailQueue": { "MessageExpirationHours": 48 }
        }
        """);

        var doc = _sut.Load();

        doc.MailQueue.MessageExpirationHours.Should().Be(48);     // from file
        doc.MailQueue.PollingIntervalSeconds.Should().Be(5);      // default
        doc.MailQueue.RetryIntervalSeconds.Should().Be(900);      // default
        doc.MailQueue.ArchiveSentEmails.Should().BeFalse();       // default
    }

    [Fact]
    public void Load_EmptyObject_ReturnsAllDefaults()
    {
        WriteJson("{}");

        var doc = _sut.Load();

        doc.Smtp.MaxSizeBytes.Should().Be(26_214_400);
        doc.MailQueue.PollingIntervalSeconds.Should().Be(5);
        doc.RawSource.Should().NotBeNull(); // file did exist → RawSource set
    }

    [Fact]
    public void Load_NullSectionValues_TreatedAsDefaults()
    {
        WriteJson("""
        {
            "GraphApi": {
                "TenantId": null,
                "ClientId": null
            }
        }
        """);

        var doc = _sut.Load();

        doc.GraphApi.TenantId.Should().BeNull();
        doc.GraphApi.ClientId.Should().BeNull();
    }

    // =========================================================================
    // Load – ENC[...] encryption
    // =========================================================================

    [Fact]
    public void Load_EncryptedClientSecret_DecryptedCorrectly()
    {
        var cipher = _protector.Protect("my-super-secret");
        WriteJson($$"""
        {
            "GraphApi": { "ClientSecret": "ENC[{{cipher}}]" }
        }
        """);

        var doc = _sut.Load();

        doc.GraphApi.ClientSecret.Should().Be("my-super-secret");
    }

    [Fact]
    public void Load_PlaintextClientSecret_ReturnedAsIs()
    {
        // Plaintext is accepted during initial setup before first Save encrypts it
        WriteJson("""{ "GraphApi": { "ClientSecret": "plain-value" } }""");

        var doc = _sut.Load();

        doc.GraphApi.ClientSecret.Should().Be("plain-value");
    }

    [Fact]
    public void Load_EncryptedUserPassword_DecryptedCorrectly()
    {
        var cipher = _protector.Protect("hunter2");
        WriteJson($$"""
        {
            "Users": [
                { "Username": "bob", "Password": "ENC[{{cipher}}]" }
            ]
        }
        """);

        var doc = _sut.Load();

        doc.Access.Users[0].Password.Should().Be("hunter2");
    }

    [Fact]
    public void Load_MultipleEncryptedUsers_EachDecryptedIndependently()
    {
        var c1 = _protector.Protect("pass1");
        var c2 = _protector.Protect("pass2");
        WriteJson($$"""
        {
            "Users": [
                { "Username": "alice", "Password": "ENC[{{c1}}]" },
                { "Username": "bob",   "Password": "ENC[{{c2}}]" }
            ]
        }
        """);

        var doc = _sut.Load();

        doc.Access.Users[0].Password.Should().Be("pass1");
        doc.Access.Users[1].Password.Should().Be("pass2");
    }

    // =========================================================================
    // Load – unknown / extra keys
    // =========================================================================

    [Fact]
    public void Load_UnknownTopLevelKey_AvailableInRawSource()
    {
        WriteJson("""
        {
            "FutureFeature": { "Setting": "preserve-me", "Count": 42 },
            "GraphApi": { "TenantId": "t1" }
        }
        """);

        var doc = _sut.Load();

        doc.RawSource.Should().NotBeNull();
        doc.RawSource!["FutureFeature"].Should().NotBeNull();
        doc.RawSource["FutureFeature"]!["Setting"]!.GetValue<string>().Should().Be("preserve-me");
        doc.RawSource["FutureFeature"]!["Count"]!.GetValue<int>().Should().Be(42);
    }

    [Fact]
    public void Load_UnknownKeyInsideKnownSection_AvailableInRawSource()
    {
        WriteJson("""
        {
            "GraphApi": {
                "TenantId": "t1",
                "FutureField": "future-value"
            }
        }
        """);

        var doc = _sut.Load();

        // Known field is read
        doc.GraphApi.TenantId.Should().Be("t1");
        // Unknown field survives in RawSource
        var ga = doc.RawSource!["GraphApi"] as JsonObject;
        ga!["FutureField"]!.GetValue<string>().Should().Be("future-value");
    }

    // =========================================================================
    // Load – corrupt / invalid JSON
    // =========================================================================

    [Fact]
    public void Load_InvalidJson_ThrowsConfigLoadException()
    {
        File.WriteAllText(_filePath, "{ not valid json !!!");

        _sut.Invoking(s => s.Load())
            .Should().Throw<ConfigLoadException>()
            .WithMessage("*Invalid JSON*");
    }

    [Fact]
    public void Load_EmptyFile_ThrowsConfigLoadException()
    {
        File.WriteAllText(_filePath, string.Empty);

        _sut.Invoking(s => s.Load())
            .Should().Throw<ConfigLoadException>()
            .WithMessage("*empty*");
    }

    [Fact]
    public void Load_WhitespaceOnlyFile_ThrowsConfigLoadException()
    {
        File.WriteAllText(_filePath, "   \r\n   ");

        _sut.Invoking(s => s.Load())
            .Should().Throw<ConfigLoadException>();
    }

    [Fact]
    public void Load_TruncatedJson_ThrowsConfigLoadException()
    {
        File.WriteAllText(_filePath, """{ "GraphApi": { "TenantId": """);

        _sut.Invoking(s => s.Load())
            .Should().Throw<ConfigLoadException>();
    }

    [Fact]
    public void Load_JsonArrayRoot_ThrowsConfigLoadException()
    {
        File.WriteAllText(_filePath, "[1, 2, 3]");

        _sut.Invoking(s => s.Load())
            .Should().Throw<ConfigLoadException>()
            .WithMessage("*JSON object*");
    }

    [Fact]
    public void Load_JsonStringRoot_ThrowsConfigLoadException()
    {
        File.WriteAllText(_filePath, "\"just a string\"");

        _sut.Invoking(s => s.Load())
            .Should().Throw<ConfigLoadException>();
    }

    [Fact]
    public void Load_CorruptEncClientSecret_ReportsFailure_AndDoesNotThrow()
    {
        WriteJson("""
        {
            "GraphApi": { "TenantId": "tenant-1", "ClientSecret": "ENC[this-is-not-a-valid-ciphertext!!!]" }
        }
        """);

        var doc = _sut.Load();

        doc.DecryptionFailures.Should().ContainSingle().Which.Should().Be("GraphApi.ClientSecret");
        doc.GraphApi.ClientSecret.Should().BeNull();        // affected field left blank
        doc.GraphApi.TenantId.Should().Be("tenant-1");      // rest of the section still loaded
    }

    [Fact]
    public void Load_CorruptEncUserPassword_ReportsFailure_WithIndex()
    {
        WriteJson("""
        {
            "Users": [
                { "Username": "alice", "Password": "ENC[corrupted-payload!!!]" }
            ]
        }
        """);

        var doc = _sut.Load();

        doc.DecryptionFailures.Should().ContainSingle().Which.Should().Contain("Users[0]");
        doc.Access.Users.Should().ContainSingle();
        doc.Access.Users[0].Username.Should().Be("alice");  // user still loaded
        doc.Access.Users[0].Password.Should().BeNull();     // only the password is blanked
    }

    [Fact]
    public void Load_CorruptEncSecondUser_LoadsFirstUser_AndReportsSecondIndex()
    {
        var goodCipher = _protector.Protect("ok");
        WriteJson($$"""
        {
            "Users": [
                { "Username": "alice", "Password": "ENC[{{goodCipher}}]" },
                { "Username": "bob",   "Password": "ENC[bad-payload!!!]"  }
            ]
        }
        """);

        var doc = _sut.Load();

        doc.DecryptionFailures.Should().ContainSingle().Which.Should().Contain("Users[1]");
        doc.Access.Users[0].Password.Should().Be("ok");     // good secret decrypted
        doc.Access.Users[1].Password.Should().BeNull();     // bad one blanked
    }

    [Fact]
    public void Load_AllSecretsValid_HasNoDecryptionFailures()
    {
        var cipher = _protector.Protect("secret");
        WriteJson($$"""
        {
            "GraphApi": { "ClientSecret": "ENC[{{cipher}}]" }
        }
        """);

        var doc = _sut.Load();

        doc.DecryptionFailures.Should().BeEmpty();
        doc.GraphApi.ClientSecret.Should().Be("secret");
    }

    // =========================================================================
    // Save – basic behaviour
    // =========================================================================

    [Fact]
    public void Save_WritesValidJsonFile()
    {
        var doc = new ConfigDocument
        {
            GraphApi = new() { TenantId = "t1", ClientId = "c1" },
            Smtp = new() { MaxSizeBytes = 5_000_000, Banner = "TestBanner" },
        };

        _sut.Save(doc);

        File.Exists(_filePath).Should().BeTrue();
        var root = ParseSaved();
        root["GraphApi"]!["TenantId"]!.GetValue<string>().Should().Be("t1");
        root["Smtp"]!["Banner"]!.GetValue<string>().Should().Be("TestBanner");
    }

    [Fact]
    public void Save_AfterSave_FileExistsReturnsTrue()
    {
        _sut.Save(new ConfigDocument());

        _sut.FileExists.Should().BeTrue();
    }

    [Fact]
    public void Save_CreatesDirectoryIfNotExists()
    {
        var subDir = Path.Combine(_dir, "config");
        var path = Path.Combine(subDir, "graphmailer.json");
        var sut = new ConfigService(path, _protector);

        sut.Save(new ConfigDocument { Smtp = new() { Banner = "DirTest" } });

        File.Exists(path).Should().BeTrue();
        Directory.Exists(subDir).Should().BeTrue();
    }

    [Fact]
    public void Save_WritesIndentedJson()
    {
        _sut.Save(new ConfigDocument());

        var json = File.ReadAllText(_filePath);
        json.Should().Contain("\n"); // indented output
    }

    // =========================================================================
    // Save – sensitive field encryption
    // =========================================================================

    [Fact]
    public void Save_ClientSecret_EncryptedAsEncBlob()
    {
        var doc = new ConfigDocument { GraphApi = new() { ClientSecret = "my-secret" } };

        _sut.Save(doc);

        var saved = ParseSaved()["GraphApi"]!["ClientSecret"]!.GetValue<string>();
        saved.Should().StartWith("ENC[").And.EndWith("]");
        // Verify the ciphertext is valid
        var cipher = saved[4..^1];
        _protector.Unprotect(cipher).Should().Be("my-secret");
    }

    [Fact]
    public void Save_UserPassword_EncryptedAsEncBlob()
    {
        var doc = new ConfigDocument
        {
            Access = new()
            {
                Users = [new() { Username = "alice", Password = "pass123" }]
            }
        };

        _sut.Save(doc);

        var saved = ParseSaved()["Users"]![0]!["Password"]!.GetValue<string>();
        saved.Should().StartWith("ENC[").And.EndWith("]");
        _protector.Unprotect(saved[4..^1]).Should().Be("pass123");
    }

    [Fact]
    public void Save_NullClientSecret_WritesNullToJson()
    {
        var doc = new ConfigDocument { GraphApi = new() { ClientSecret = null } };

        _sut.Save(doc);

        ParseSaved()["GraphApi"]!["ClientSecret"].Should().BeNull();
    }

    [Fact]
    public void Save_NullUserPassword_WritesNullToJson()
    {
        var doc = new ConfigDocument
        {
            Access = new()
            {
                Users = [new() { Username = "alice", Password = null }]
            }
        };

        _sut.Save(doc);

        ParseSaved()["Users"]![0]!["Password"].Should().BeNull();
    }

    [Fact]
    public void Save_NonSensitiveFields_NotEncrypted()
    {
        var doc = new ConfigDocument
        {
            GraphApi = new() { TenantId = "plain-tenant", ClientCertificateThumbprint = "AABB" }
        };

        _sut.Save(doc);

        var root = ParseSaved();
        root["GraphApi"]!["TenantId"]!.GetValue<string>().Should().Be("plain-tenant");
        root["GraphApi"]!["ClientCertificateThumbprint"]!.GetValue<string>().Should().Be("AABB");
    }

    // =========================================================================
    // Save – atomic write
    // =========================================================================

    [Fact]
    public void Save_Atomic_NoTempFileAfterSuccessfulWrite()
    {
        _sut.Save(new ConfigDocument { Smtp = new() { Banner = "Atomic" } });

        File.Exists(_filePath + ".tmp").Should().BeFalse();
        File.Exists(_filePath).Should().BeTrue();
    }

    // =========================================================================
    // Save – unknown key preservation
    // =========================================================================

    [Fact]
    public void Save_PreservesUnknownTopLevelKeysFromRawSource()
    {
        WriteJson("""
        {
            "FutureFeature": { "Setting": "preserve-me" },
            "Smtp": { "Banner": "Original" }
        }
        """);

        var doc = _sut.Load();
        doc.Smtp.Banner = "Updated";
        _sut.Save(doc);

        var root = ParseSaved();
        root["FutureFeature"]!["Setting"]!.GetValue<string>().Should().Be("preserve-me");
        root["Smtp"]!["Banner"]!.GetValue<string>().Should().Be("Updated");
    }

    [Fact]
    public void Save_PreservesUnknownKeysInsideKnownSection()
    {
        WriteJson("""
        {
            "GraphApi": {
                "TenantId": "t1",
                "FutureGraphApiField": "keep-me"
            }
        }
        """);

        var doc = _sut.Load();
        doc.GraphApi.ClientId = "c1";
        _sut.Save(doc);

        var root = ParseSaved();
        root["GraphApi"]!["TenantId"]!.GetValue<string>().Should().Be("t1");
        root["GraphApi"]!["ClientId"]!.GetValue<string>().Should().Be("c1");
        root["GraphApi"]!["FutureGraphApiField"]!.GetValue<string>().Should().Be("keep-me");
    }

    // =========================================================================
    // Round-trip: Load → Save → Load
    // =========================================================================

    [Fact]
    public void RoundTrip_AllKnownFields_RestoredIdentically()
    {
        WriteJson("""
        {
            "GraphApi": { "TenantId": "t1", "ClientId": "c1", "ClientCertificateThumbprint": "AABB" },
            "Smtp": { "MaxSizeBytes": 5242880, "Banner": "RT" },
            "Certificate": { "SubjectName": "smtp.corp.com", "Issuer": "My CA" },
            "MailQueue": { "MessageExpirationHours": 48, "ArchiveSentEmails": true, "SentEmailRetentionDays": 30 },
            "Servers": [{ "Name": "S1", "Port": 2526, "Mode": "Plain", "AuthRequired": false }],
            "IpWhitelist": ["10.0.0.1"],
            "AllowedSenders": ["sender@corp.com"],
            "Users": [{ "Username": "user1", "Password": "pw", "FromRestrictions": ["user1@corp.com"] }]
        }
        """);

        _sut.Save(_sut.Load());
        var doc = _sut.Load();

        doc.GraphApi.TenantId.Should().Be("t1");
        doc.GraphApi.ClientId.Should().Be("c1");
        doc.GraphApi.ClientCertificateThumbprint.Should().Be("AABB");
        doc.Smtp.Banner.Should().Be("RT");
        doc.Smtp.MaxSizeBytes.Should().Be(5_242_880);
        doc.Certificate.SubjectName.Should().Be("smtp.corp.com");
        doc.Certificate.Issuer.Should().Be("My CA");
        doc.MailQueue.MessageExpirationHours.Should().Be(48);
        doc.MailQueue.ArchiveSentEmails.Should().BeTrue();
        doc.MailQueue.SentEmailRetentionDays.Should().Be(30);
        doc.Servers.Should().HaveCount(1);
        doc.Servers[0].Port.Should().Be(2526);
        doc.Access.IpWhitelist.Should().ContainSingle("10.0.0.1");
        doc.Access.AllowedSenders.Should().ContainSingle("sender@corp.com");
        doc.Access.Users[0].Username.Should().Be("user1");
        doc.Access.Users[0].Password.Should().Be("pw");
        doc.Access.Users[0].FromRestrictions.Should().ContainSingle("user1@corp.com");
    }

    [Fact]
    public void Save_StampsCurrentSchemaVersion()
    {
        _sut.Save(new ConfigDocument());

        _sut.Load().SchemaVersion.Should().Be(ConfigSchema.Current);
    }

    [Fact]
    public void RoundTrip_EncryptedClientSecret_StableOverMultipleSaveCycles()
    {
        var doc = new ConfigDocument { GraphApi = new() { ClientSecret = "stable-secret" } };
        _sut.Save(doc);

        // Second cycle
        var loaded = _sut.Load();
        loaded.GraphApi.ClientSecret.Should().Be("stable-secret");
        _sut.Save(loaded);

        // Third cycle
        var loaded2 = _sut.Load();
        loaded2.GraphApi.ClientSecret.Should().Be("stable-secret");
    }

    [Fact]
    public void RoundTrip_EncryptedUserPassword_StableOverMultipleSaveCycles()
    {
        var doc = new ConfigDocument
        {
            Access = new()
            {
                Users = [new() { Username = "charlie", Password = "safe-pass" }]
            }
        };
        _sut.Save(doc);

        var loaded = _sut.Load();
        loaded.Access.Users[0].Password.Should().Be("safe-pass");

        _sut.Save(loaded);
        var loaded2 = _sut.Load();
        loaded2.Access.Users[0].Password.Should().Be("safe-pass");
    }

    [Fact]
    public void RoundTrip_UnknownTopLevelKey_SurvivesMultipleSaveCycles()
    {
        WriteJson("""
        {
            "FutureFeature": "still-here",
            "Smtp": { "Banner": "X" }
        }
        """);

        // First cycle
        _sut.Save(_sut.Load());

        // Second cycle
        _sut.Save(_sut.Load());

        ParseSaved()["FutureFeature"]!.GetValue<string>().Should().Be("still-here");
    }

    [Fact]
    public void RoundTrip_EmptyArrays_RemainEmpty()
    {
        var doc = new ConfigDocument
        {
            Access = new()
            {
                IpWhitelist = [],
                Users = []
            }
        };
        _sut.Save(doc);

        var loaded = _sut.Load();

        loaded.Access.IpWhitelist.Should().BeEmpty();
        loaded.Access.Users.Should().BeEmpty();
    }

    [Fact]
    public void Backup_SaveThenLoad_RoundTripsValues_AndDecryptsPassword()
    {
        var doc = new ConfigDocument
        {
            Backup = new ConfigDocument.BackupSection
            {
                BackupEnabled = true,
                Frequency = "Weekly",
                TimeOfDay = "03:00",
                DayOfWeek = "Sunday",
                MaxBackups = 30,
                Directory = @"E:\gm-backups",
                Password = "the-backup-password",
                EmailEnabled = true,
                EmailRecipients = ["ops@corp.com"],
            },
        };

        _sut.Save(doc);
        var loaded = _sut.Load().Backup;

        loaded.BackupEnabled.Should().BeTrue();
        loaded.Frequency.Should().Be("Weekly");
        loaded.TimeOfDay.Should().Be("03:00");
        loaded.DayOfWeek.Should().Be("Sunday");
        loaded.MaxBackups.Should().Be(30);
        loaded.Directory.Should().Be(@"E:\gm-backups");
        loaded.Password.Should().Be("the-backup-password");   // ENC[…] written, decrypted back
        loaded.EmailEnabled.Should().BeTrue();
        loaded.EmailRecipients.Should().ContainSingle().Which.Should().Be("ops@corp.com");
    }

    // =========================================================================
    // Defaults overlay – bundled appsettings.json seeds defaults for omitted fields
    // =========================================================================

    [Fact]
    public void Load_OmittedScalar_TakesDefaultFromAppSettings_NotHardCodedLiteral()
    {
        // appsettings.json (the shared default source) carries a non-literal default; a config
        // that omits the field must inherit it via the overlay rather than the code fallback.
        var appSettings = Path.Combine(_dir, "appsettings.json");
        File.WriteAllText(appSettings, """{ "Smtp": { "MaxSizeBytes": 9999, "Banner": "FromAppSettings" } }""");
        var sut = new ConfigService(_filePath, _protector, appSettings);
        WriteJson("""{ "GraphApi": { "TenantId": "t1" } }""");

        var doc = sut.Load();

        doc.Smtp.MaxSizeBytes.Should().Be(9999);
        doc.Smtp.Banner.Should().Be("FromAppSettings");
    }

    [Fact]
    public void Load_UserValue_OverridesAppSettingsDefault()
    {
        var appSettings = Path.Combine(_dir, "appsettings.json");
        File.WriteAllText(appSettings, """{ "Smtp": { "Banner": "FromAppSettings" } }""");
        var sut = new ConfigService(_filePath, _protector, appSettings);
        WriteJson("""{ "Smtp": { "Banner": "UserWins" } }""");

        sut.Load().Smtp.Banner.Should().Be("UserWins");
    }

    [Fact]
    public void Load_DefaultsOverlay_DoesNotMaterialiseIntoRawSource()
    {
        var appSettings = Path.Combine(_dir, "appsettings.json");
        File.WriteAllText(appSettings, """{ "Smtp": { "Banner": "FromAppSettings" } }""");
        var sut = new ConfigService(_filePath, _protector, appSettings);
        WriteJson("""{ "GraphApi": { "TenantId": "t1" } }""");

        var doc = sut.Load();

        // The overlay feeds Read*, but RawSource stays the untouched user document.
        (doc.RawSource!["Smtp"]).Should().BeNull();
    }

    [Fact]
    public void MergeDefaults_ObjectsRecurse_ScalarsAndArraysReplaced()
    {
        var defaults = (JsonObject)JsonNode.Parse("""
            { "Obj": { "A": 1, "B": 2 }, "Scalar": "d", "Arr": [1, 2, 3] }
            """)!;
        var user = (JsonObject)JsonNode.Parse("""
            { "Obj": { "B": 22, "C": 33 }, "Scalar": "u", "Arr": [] }
            """)!;

        var merged = ConfigService.MergeDefaults(defaults, user);

        merged["Obj"]!["A"]!.GetValue<int>().Should().Be(1);   // default kept
        merged["Obj"]!["B"]!.GetValue<int>().Should().Be(22);  // user overrides
        merged["Obj"]!["C"]!.GetValue<int>().Should().Be(33);  // user adds
        merged["Scalar"]!.GetValue<string>().Should().Be("u"); // user wins
        merged["Arr"]!.AsArray().Should().BeEmpty();           // user empty array replaces (no index-merge)
    }

    // =========================================================================
    // UpdateDismissedRecommendations – targeted partial write
    // =========================================================================

    [Fact]
    public void UpdateDismissedRecommendations_WritesOnlyThatKey_LeavingEverythingElseUntouched()
    {
        // The ConfigTool calls this while the user may have unsaved edits on other pages:
        // nothing outside Recommendations.Dismissed may change.
        WriteJson("""
        {
            "SchemaVersion": 5,
            "Smtp": { "Banner": "original" },
            "CustomThirdPartyKey": { "keep": "me" }
        }
        """);

        _sut.UpdateDismissedRecommendations(["telemetry", "log-level"]);

        var saved = ParseSaved();
        saved["Recommendations"]!["Dismissed"]!.AsArray()
            .Select(n => n!.GetValue<string>()).Should().Equal("telemetry", "log-level");
        saved["Smtp"]!["Banner"]!.GetValue<string>().Should().Be("original");
        saved["CustomThirdPartyKey"]!["keep"]!.GetValue<string>().Should().Be("me");
    }

    [Fact]
    public void UpdateDismissedRecommendations_DoesNotDisturbEncryptedSecrets()
    {
        // A partial write must never round-trip secrets through decrypt/re-encrypt: the
        // ciphertext on disk has to stay byte-identical.
        _sut.Save(new ConfigDocument { GraphApi = new() { ClientSecret = "top-secret" } });
        var before = ParseSaved()["GraphApi"]!["ClientSecret"]!.GetValue<string>();

        _sut.UpdateDismissedRecommendations(["telemetry"]);

        ParseSaved()["GraphApi"]!["ClientSecret"]!.GetValue<string>().Should().Be(before);
        _sut.Load().GraphApi.ClientSecret.Should().Be("top-secret");
    }

    [Fact]
    public void UpdateDismissedRecommendations_EmptyList_ClearsThePreviousSelection()
    {
        WriteJson("""{ "Recommendations": { "Dismissed": [ "telemetry" ] } }""");

        _sut.UpdateDismissedRecommendations([]);

        _sut.Load().Recommendations.Dismissed.Should().BeEmpty();
    }

    [Fact]
    public void UpdateDismissedRecommendations_NoConfigFileYet_IsANoOp()
    {
        // First run: the document only lives in memory. Writing a half-empty config here would
        // shadow the appsettings.json defaults — the caller's next Save persists the ids instead.
        _sut.UpdateDismissedRecommendations(["telemetry"]);

        File.Exists(_filePath).Should().BeFalse();
    }

    [Fact]
    public void UpdateDismissedRecommendations_ThenSave_KeepsTheDismissedIds()
    {
        // Save() rebuilds from RawSource, which predates the partial write — the caller keeps the
        // ids in its ConfigDocument, and this asserts the two paths agree on the result.
        _sut.Save(new ConfigDocument { Smtp = new() { Banner = "first" } });
        _sut.UpdateDismissedRecommendations(["telemetry"]);

        var doc = _sut.Load();
        doc.Recommendations.Dismissed.Should().Equal("telemetry");

        doc.Smtp.Banner = "second";
        _sut.Save(doc);

        var reloaded = _sut.Load();
        reloaded.Recommendations.Dismissed.Should().Equal("telemetry");
        reloaded.Smtp.Banner.Should().Be("second");
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private void WriteJson(string json)
        => File.WriteAllText(_filePath, json, System.Text.Encoding.UTF8);

    private JsonObject ParseSaved()
    {
        var json = File.ReadAllText(_filePath);
        return (JsonObject)JsonNode.Parse(json)!;
    }
}
