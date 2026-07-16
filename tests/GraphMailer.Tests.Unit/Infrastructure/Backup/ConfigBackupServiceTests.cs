using System.Text.Json.Nodes;
using GraphMailer.Service.Infrastructure.Backup;
using GraphMailer.Service.Infrastructure.Config;
using GraphMailer.Service.Infrastructure.Encryption;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace GraphMailer.Tests.Unit.Infrastructure.Backup;

public sealed class ConfigBackupServiceTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly IDataProtector _protector;
    private readonly string _dir;

    public ConfigBackupServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"backup-svc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName(DataProtectionExtensions.ApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_dir, "keys")));
        _sp = services.BuildServiceProvider();
        _protector = _sp.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(DataProtectionExtensions.ConfigPurpose);
    }

    public void Dispose()
    {
        _sp.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string WriteSourceConfig(string clientSecret, string userPassword)
    {
        var path = Path.Combine(_dir, "graphmailer.json");
        var json = $$"""
        {
          "GraphApi": { "TenantId": "tenant-1", "ClientSecret": "ENC[{{_protector.Protect(clientSecret)}}]" },
          "Users": [ { "Username": "alice", "Password": "ENC[{{_protector.Protect(userPassword)}}]" } ]
        }
        """;
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void BuildBackup_ThenReadManifest_RoundTrips()
    {
        var sut = new ConfigBackupService(_protector, WriteSourceConfig("sek-ret", "pw1"));

        var bytes = sut.BuildBackup("backup-pass");
        var manifest = sut.ReadManifest(bytes, "backup-pass");

        manifest.SourceMachine.Should().Be(Environment.MachineName);
        manifest.CreatedUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void ReadManifest_WrongPassword_Throws()
    {
        var sut = new ConfigBackupService(_protector, WriteSourceConfig("sek-ret", "pw1"));
        var bytes = sut.BuildBackup("backup-pass");

        var act = () => sut.ReadManifest(bytes, "nope");

        act.Should().Throw<BackupDecryptionException>();
    }

    [Fact]
    public void Restore_RewritesConfig_WithSecretsReEncryptedUnderLocalKey()
    {
        var source = new ConfigBackupService(_protector, WriteSourceConfig("sek-ret", "pw1"));
        var bytes = source.BuildBackup("backup-pass");

        var targetPath = Path.Combine(_dir, "restored", "graphmailer.json");
        var target = new ConfigBackupService(_protector, targetPath);

        target.Restore(bytes, "backup-pass");

        // Restored file exists and stores secrets as ENC[...] (not plaintext)
        var raw = JsonNode.Parse(File.ReadAllText(targetPath))!;
        raw["GraphApi"]!["ClientSecret"]!.GetValue<string>().Should().StartWith("ENC[");

        // …and those secrets decrypt back to the originals via ConfigService
        var doc = new ConfigService(targetPath, _protector).Load();
        doc.GraphApi.ClientSecret.Should().Be("sek-ret");
        doc.Access.Users[0].Password.Should().Be("pw1");
        doc.GraphApi.TenantId.Should().Be("tenant-1");
    }

    [Fact]
    public void Rotate_KeepsNewest_DeletesOldest()
    {
        var dir = Path.Combine(_dir, "store");
        Directory.CreateDirectory(dir);
        // Create 5 backup files with increasing timestamps
        for (var i = 0; i < 5; i++)
        {
            var f = Path.Combine(dir, $"graphmailer-backup-2026010{i}-000000.gmbak");
            File.WriteAllText(f, "x");
            File.SetLastWriteTimeUtc(f, new DateTime(2026, 1, 1 + i, 0, 0, 0, DateTimeKind.Utc));
        }
        var sut = new ConfigBackupService(_protector, Path.Combine(_dir, "graphmailer.json"));

        var deleted = sut.Rotate(dir, maxBackups: 3);

        deleted.Should().Be(2);
        var remaining = Directory.GetFiles(dir, "*.gmbak").Select(Path.GetFileName).ToList();
        remaining.Should().HaveCount(3);
        remaining.Should().Contain("graphmailer-backup-20260104-000000.gmbak");   // newest kept
        remaining.Should().NotContain("graphmailer-backup-20260100-000000.gmbak"); // oldest gone
    }

    [Fact]
    public void Rotate_FewerThanMax_DeletesNothing()
    {
        var dir = Path.Combine(_dir, "store2");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "graphmailer-backup-20260101-000000.gmbak"), "x");
        var sut = new ConfigBackupService(_protector, Path.Combine(_dir, "graphmailer.json"));

        sut.Rotate(dir, maxBackups: 5).Should().Be(0);
    }
}
