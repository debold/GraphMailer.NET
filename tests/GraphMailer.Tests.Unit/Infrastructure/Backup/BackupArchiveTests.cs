using GraphMailer.Service.Infrastructure.Backup;

namespace GraphMailer.Tests.Unit.Infrastructure.Backup;

public sealed class BackupArchiveTests
{
    [Fact]
    public void BuildThenRead_RoundTripsManifestAndConfig()
    {
        var manifest = new BackupManifest
        {
            CreatedUtc = new DateTimeOffset(2026, 6, 13, 3, 0, 0, TimeSpan.Zero),
            SourceMachine = "HOST-1",
            AppVersion = "1.2.3",
        };
        var configJson = """{ "GraphApi": { "TenantId": "abc" } }""";

        var zip = BackupArchive.Build(manifest, configJson);
        var (readManifest, readConfig) = BackupArchive.Read(zip);

        readConfig.Should().Be(configJson);
        readManifest.SourceMachine.Should().Be("HOST-1");
        readManifest.AppVersion.Should().Be("1.2.3");
        readManifest.CreatedUtc.Should().Be(manifest.CreatedUtc);
    }

    [Fact]
    public void Read_NonArchiveBytes_ThrowsFormatException()
    {
        var act = () => BackupArchive.Read([0x00, 0x01, 0x02, 0x03]);

        act.Should().Throw<BackupFormatException>();
    }

    [Fact]
    public void EncryptedRoundTrip_ThroughCrypto_PreservesConfig()
    {
        // The realistic path: archive → encrypt → decrypt → archive read.
        var configJson = """{ "Users": [ { "Username": "alice", "Password": "pw" } ] }""";
        var zip = BackupArchive.Build(new BackupManifest { CreatedUtc = DateTimeOffset.UtcNow }, configJson);

        var container = BackupCrypto.Encrypt(zip, "backup-pass", iterations: 1_000);
        var restoredZip = BackupCrypto.Decrypt(container, "backup-pass");
        var (_, restoredConfig) = BackupArchive.Read(restoredZip);

        restoredConfig.Should().Be(configJson);
    }
}
