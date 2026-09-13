using System.Text.Json;
using GraphMailer.ConfigTool.Helpers;
using GraphMailer.Service.Services;

namespace GraphMailer.Tests.Unit.ConfigTool;

/// <summary>
/// Tests for <see cref="DetectionRecordReader"/> — the selection behind *Recent Detections* on the
/// Malware Scan page.
///
/// The point of contention is that <c>mail\blocked\</c> is shared with the message rules: both
/// subsystems write their records there, interleaved in time. Everything here guards the
/// consequence — the display limit counts detections, never files, or a burst of rule discards
/// would push real findings out of a list that then claims there are none.
/// </summary>
public sealed class DetectionRecordReaderTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "detectionreader-tests-" + Guid.NewGuid().ToString("N"));

    public DetectionRecordReaderTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// Writes one record. The reader orders by last-write time, so the timestamp is set explicitly
    /// rather than left to however fast the test machine creates files.
    /// </summary>
    private void Write(string id, string source, DateTime writtenUtc, string from = "sender@example.com")
    {
        var path = Path.Combine(_dir, $"{id}.meta.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new BlockedMessageRecord
        {
            Source = source,
            MessageId = id,
            From = from,
            DetectedAt = writtenUtc,
            PartName = "invoice.docm",
        }));
        File.SetLastWriteTimeUtc(path, writtenUtc);
    }

    private static readonly DateTime Base = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    // =========================================================================

    [Fact]
    public void Read_MissingDirectory_ReturnsEmpty()
    {
        var result = DetectionRecordReader.Read(Path.Combine(_dir, "does-not-exist"));

        result.Records.Should().BeEmpty();
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Read_RuleDiscards_AreNotDetections()
    {
        Write("a", BlockedMessageSources.MessageRule, Base);

        DetectionRecordReader.Read(_dir).Records.Should().BeEmpty(
            "a message a rule discarded was never flagged by the scanner");
    }

    [Fact]
    public void Read_RecordWithoutSource_CountsAsDetection()
    {
        // Records written before the Source field existed all came from the scanner.
        var path = Path.Combine(_dir, "legacy.meta.json");
        File.WriteAllText(path, """{"MessageId":"legacy","From":"old@example.com","PartName":"x.exe"}""");

        DetectionRecordReader.Read(_dir).Records.Should().ContainSingle();
    }

    [Fact]
    public void Read_DetectionOlderThanManyRuleDiscards_IsStillFound()
    {
        // The regression this reader exists for: the newest files are rule discards, and cutting
        // the list to the newest N *files* hid the detection sitting behind them entirely.
        Write("detection", BlockedMessageSources.MalwareScan, Base);
        for (var i = 1; i <= DetectionRecordReader.MaxDetectionsShown + 50; i++)
            Write($"discard-{i}", BlockedMessageSources.MessageRule, Base.AddMinutes(i));

        var result = DetectionRecordReader.Read(_dir);

        result.Records.Should().ContainSingle(
            "the display limit counts detections, not files");
        result.Records[0].MessageId.Should().Be("detection");
        result.Truncated.Should().BeFalse("the folder is far below the examination limit");
    }

    [Fact]
    public void Read_ReturnsNewestFirst()
    {
        Write("old", BlockedMessageSources.MalwareScan, Base);
        Write("new", BlockedMessageSources.MalwareScan, Base.AddHours(1));

        DetectionRecordReader.Read(_dir).Records
            .Select(r => r.MessageId).Should().Equal("new", "old");
    }

    [Fact]
    public void Read_MoreDetectionsThanTheLimit_ReturnsTheNewestOnes()
    {
        for (var i = 0; i < DetectionRecordReader.MaxDetectionsShown + 10; i++)
            Write($"d-{i:000}", BlockedMessageSources.MalwareScan, Base.AddMinutes(i));

        var result = DetectionRecordReader.Read(_dir);

        result.Records.Should().HaveCount(DetectionRecordReader.MaxDetectionsShown);
        result.Records[0].MessageId.Should().Be("d-209", "the newest detection comes first");
    }

    [Fact]
    public void Read_UnreadableRecord_DoesNotHideTheRest()
    {
        File.WriteAllText(Path.Combine(_dir, "broken.meta.json"), "{ not json");
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "broken.meta.json"), Base.AddHours(1));
        Write("good", BlockedMessageSources.MalwareScan, Base);

        DetectionRecordReader.Read(_dir).Records.Should().ContainSingle()
            .Which.MessageId.Should().Be("good");
    }

    [Fact]
    public void Read_SearchStoppedAtTheExaminationLimit_ReportsTruncated()
    {
        // Nothing but discards within reach: the reader gives up rather than walking a folder a
        // busy rule filled, and has to say so — the caller must not read "no records" as "none exist".
        for (var i = 0; i < 5; i++)
            Write($"discard-{i}", BlockedMessageSources.MessageRule, Base.AddMinutes(i));
        Write("detection", BlockedMessageSources.MalwareScan, Base.AddMinutes(-1));

        var result = DetectionRecordReader.Read(_dir, maxRecordsExamined: 3);

        result.Records.Should().BeEmpty();
        result.Truncated.Should().BeTrue();
    }

    [Fact]
    public void Read_WholeFolderRead_IsNotReportedAsTruncated()
    {
        Write("detection", BlockedMessageSources.MalwareScan, Base);
        Write("discard", BlockedMessageSources.MessageRule, Base.AddMinutes(1));

        DetectionRecordReader.Read(_dir, maxRecordsExamined: 2).Truncated.Should().BeFalse(
            "exactly as many files as the limit allows is a complete search, not a cut-off one");
    }

    // ── Caption ──────────────────────────────────────────────────────────────

    [Fact]
    public void Describe_NothingFoundAndNothingSkipped_SaysTheFolderIsEmpty()
        => DetectionRecordReader.Describe(0, truncated: false).Should().Be("No detections recorded.");

    [Fact]
    public void Describe_NothingFoundAfterATruncatedSearch_DoesNotClaimTheFolderIsEmpty()
    {
        // The claim would cover records the search never opened.
        var text = DetectionRecordReader.Describe(0, truncated: true);

        text.Should().NotBe("No detections recorded.");
        text.Should().Contain("newest");
    }

    [Fact]
    public void Describe_FewerThanTheLimit_ReportsThePlainCount()
        => DetectionRecordReader.Describe(3, truncated: false).Should().Be("3 detection(s)");

    [Fact]
    public void Describe_AtTheLimit_SaysTheListIsCut()
        => DetectionRecordReader.Describe(DetectionRecordReader.MaxDetectionsShown, truncated: false)
            .Should().Contain("newest shown");
}
