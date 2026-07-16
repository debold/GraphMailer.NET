using GraphMailer.Service.Services;

namespace GraphMailer.Tests.Unit.Services;

/// <summary>
/// Tests for the sync status file shared between the service (writer) and the
/// ConfigTool Access Control page (reader).
/// </summary>
public sealed class SenderDirectoryStatusTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "sync-status-tests-" + Guid.NewGuid().ToString("N"));

    public SenderDirectoryStatusTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void SaveAndTryLoad_RoundTripsAllFields()
    {
        var path = Path.Combine(_dir, "status.json");
        var sync = new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc);
        new SenderDirectoryStatus
        {
            LastSyncUtc = sync,
            LastSyncSuccess = true,
            UserCount = 42,
            AddressCount = 99,
            NextSyncUtc = sync.AddMinutes(60),
        }.Save(path);

        var loaded = SenderDirectoryStatus.TryLoad(path);

        loaded.Should().NotBeNull();
        loaded!.LastSyncUtc.Should().Be(sync);
        loaded.LastSyncSuccess.Should().BeTrue();
        loaded.UserCount.Should().Be(42);
        loaded.AddressCount.Should().Be(99);
        loaded.LastError.Should().BeNull();
        loaded.NextSyncUtc.Should().Be(sync.AddMinutes(60));
    }

    [Fact]
    public void TryLoad_MissingFile_ReturnsNull()
    {
        SenderDirectoryStatus.TryLoad(Path.Combine(_dir, "does-not-exist.json"))
            .Should().BeNull();
    }

    [Fact]
    public void TryLoad_CorruptFile_ReturnsNull()
    {
        var path = Path.Combine(_dir, "corrupt.json");
        File.WriteAllText(path, "{ not json !!!");

        SenderDirectoryStatus.TryLoad(path).Should().BeNull();
    }
}
