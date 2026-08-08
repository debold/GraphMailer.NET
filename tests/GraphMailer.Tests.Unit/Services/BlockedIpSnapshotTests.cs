using System.IO;
using GraphMailer.ConfigTool.Views;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Security;
using GraphMailer.Service.Services;
using GraphMailer.Tests.Unit.Infrastructure.Security;   // FakeTimeProvider, TestOptionsMonitor
using Microsoft.Extensions.Logging.Abstractions;

namespace GraphMailer.Tests.Unit.Services;

/// <summary>
/// File bridge that makes the service's in-memory IP blocks visible to the ConfigTool. Before
/// this, the ConfigTool's "Currently Blocked IPs" list was a stub that always showed nothing —
/// while the help page described it as a live view.
/// </summary>
public sealed class BlockedIpSnapshotTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "gm-blockedips-" + Guid.NewGuid().ToString("N"));

    private string Path_ => System.IO.Path.Combine(_dir, "blocked-ips.json");

    public BlockedIpSnapshotTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static IpBlockingService Service(string? snapshotPath, TimeProvider? clock = null)
        => new(
            new TestOptionsMonitor<IpBlockingProtectionOptions>(new IpBlockingProtectionOptions
            {
                Enabled = true,
                FailureThreshold = 3,
                TimeframeSeconds = 600,
                BlockDurationSeconds = 600,
            }),
            NullLogger<IpBlockingService>.Instance,
            clock,
            snapshotPath);

    // ── Round-trip ───────────────────────────────────────────────────────────

    [Fact]
    public void Save_ThenLoad_PreservesEveryDisplayedField()
    {
        var written = new BlockedIpSnapshot
        {
            WrittenAtUtc = new DateTime(2026, 8, 8, 10, 0, 0, DateTimeKind.Utc),
            Entries =
            [
                new BlockedIpEntry
                {
                    Ip = "10.0.0.7",
                    Failures = 12,
                    BlockedAtUtc = new DateTime(2026, 8, 8, 9, 55, 0, DateTimeKind.Utc),
                    ExpiresAtUtc = new DateTime(2026, 8, 8, 10, 5, 0, DateTimeKind.Utc),
                },
            ],
        };
        written.Save(Path_);

        var loaded = BlockedIpSnapshot.TryLoad(Path_)!;

        loaded.Entries.Should().ContainSingle();
        loaded.Entries[0].Ip.Should().Be("10.0.0.7");
        loaded.Entries[0].Failures.Should().Be(12, "the help page promises the failure count");
        loaded.Entries[0].BlockedAtUtc.Should().Be(written.Entries[0].BlockedAtUtc);
        loaded.Entries[0].ExpiresAtUtc.Should().Be(written.Entries[0].ExpiresAtUtc);
    }

    [Fact]
    public void TryLoad_MissingFile_ReturnsNull()
    {
        BlockedIpSnapshot.TryLoad(Path_).Should().BeNull(
            "a service that never blocked anything writes no file — that is not an error");
    }

    [Fact]
    public void TryLoad_CorruptFile_ReturnsNullInsteadOfThrowing()
    {
        File.WriteAllText(Path_, "{ not json");

        BlockedIpSnapshot.TryLoad(Path_).Should().BeNull();
    }

    [Fact]
    public void Save_ReplacesTheEarlierSnapshot()
    {
        new BlockedIpSnapshot { Entries = [new BlockedIpEntry { Ip = "10.0.0.1" }] }.Save(Path_);
        new BlockedIpSnapshot { Entries = [new BlockedIpEntry { Ip = "10.0.0.2" }] }.Save(Path_);

        BlockedIpSnapshot.TryLoad(Path_)!.Entries.Should().ContainSingle(e => e.Ip == "10.0.0.2");
    }

    // ── Expiry filtering ─────────────────────────────────────────────────────

    [Fact]
    public void ActiveAt_DropsEntriesWhoseBlockHasRunOut()
    {
        var now = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        var snapshot = new BlockedIpSnapshot
        {
            Entries =
            [
                new BlockedIpEntry { Ip = "10.0.0.1", ExpiresAtUtc = now.AddMinutes(-1) },
                new BlockedIpEntry { Ip = "10.0.0.2", ExpiresAtUtc = now.AddMinutes(5) },
            ],
        };

        snapshot.ActiveAt(now).Should().ContainSingle(e => e.Ip == "10.0.0.2",
            "the file is only rewritten on change, so it outlives the blocks it lists");
    }

    [Fact]
    public void ActiveAt_StaleFileFromAStoppedService_ShowsNothing()
    {
        var blockedAt = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var snapshot = new BlockedIpSnapshot
        {
            WrittenAtUtc = blockedAt,
            Entries = [new BlockedIpEntry { Ip = "10.0.0.1", ExpiresAtUtc = blockedAt.AddMinutes(10) }],
        };

        snapshot.ActiveAt(blockedAt.AddDays(7)).Should().BeEmpty(
            "a file left behind by a stopped service must not read as a live block");
    }

    // ── Publishing from the blocking service ─────────────────────────────────

    [Fact]
    public void RecordFailure_ReachingTheThreshold_PublishesTheBlock()
    {
        using var sut = Service(Path_);

        for (var i = 0; i < 3; i++) sut.RecordFailure("10.9.9.9", "authFailure");

        var loaded = BlockedIpSnapshot.TryLoad(Path_)!;
        loaded.Entries.Should().ContainSingle(e => e.Ip == "10.9.9.9");
        loaded.Entries[0].Failures.Should().Be(3, "the count that tripped the threshold is shown");
    }

    [Fact]
    public void RecordFailure_BelowTheThreshold_WritesNothing()
    {
        using var sut = Service(Path_);

        sut.RecordFailure("10.9.9.9", "authFailure");

        File.Exists(Path_).Should().BeFalse("only an actual block is worth reporting");
    }

    [Fact]
    public void Sweep_AfterTheBlockExpired_PublishesTheEmptyList()
    {
        var clock = new FakeTimeProvider(new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc));
        using var sut = Service(Path_, clock);

        for (var i = 0; i < 3; i++) sut.RecordFailure("10.9.9.9", "authFailure");
        BlockedIpSnapshot.TryLoad(Path_)!.Entries.Should().ContainSingle();

        clock.Advance(TimeSpan.FromMinutes(11));
        sut.Sweep();

        BlockedIpSnapshot.TryLoad(Path_)!.Entries.Should().BeEmpty(
            "an expired block must disappear from the file, not only from memory");
    }

    [Fact]
    public void Constructor_WithoutASnapshotPath_NeverTouchesTheDisk()
    {
        using var sut = Service(snapshotPath: null);

        for (var i = 0; i < 3; i++) sut.RecordFailure("10.9.9.9", "authFailure");

        Directory.GetFiles(_dir).Should().BeEmpty(
            "publishing is opt-in so unit tests do not write into %ProgramData%");
    }

    // ── ConfigTool staleness label ───────────────────────────────────────────

    [Fact]
    public void AgeLabel_NoSnapshotYet_SaysSoRatherThanClaimingItIsCurrent()
    {
        IpFilteringPage.BlockedAgeLabel(null, DateTime.UtcNow)
            .Should().Be("The service has not reported any blocks yet.");
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(5, "5 min ago")]
    [InlineData(180, "3 h ago")]
    [InlineData(4320, "3 d ago")]
    public void AgeLabel_NamesHowOldTheInformationIs(int ageMinutes, string expected)
    {
        var now = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);

        IpFilteringPage.BlockedAgeLabel(now.AddMinutes(-ageMinutes), now)
            .Should().Be($"Last reported by the service {expected}.");
    }
}
