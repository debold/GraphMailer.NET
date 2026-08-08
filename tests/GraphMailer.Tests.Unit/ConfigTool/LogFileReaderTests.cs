using System.IO;
using GraphMailer.ConfigTool.Services;

namespace GraphMailer.Tests.Unit.ConfigTool;

/// <summary>
/// Reader behind the ConfigTool's Logs page. It pages through all retained rolling files
/// newest-first; the page used to read only the newest two and silently keep 2000 lines, so an
/// entry from three days ago was unreachable and unsearchable.
/// </summary>
public sealed class LogFileReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "gm-logreader-" + Guid.NewGuid().ToString("N"));

    public LogFileReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void WriteLog(string date, params string[] lines)
        => File.WriteAllLines(Path.Combine(_dir, $"graphmailer-{date}.log"), lines);

    private static string Line(string level, string component, string message, string time = "10:30:00.123")
        => $"2026-08-08 {time} +02:00 [{level}] [{component}] {message}";

    // ── Parsing ──────────────────────────────────────────────────────────────

    [Fact]
    public void Read_MissingDirectory_ReturnsEmptyResult()
    {
        var result = LogFileReader.Read(Path.Combine(_dir, "does-not-exist"), 100, null);

        result.Entries.Should().BeEmpty();
        result.HasMore.Should().BeFalse();
        result.Scanned.Should().Be(0);
    }

    [Fact]
    public void Read_SplitsLevelComponentAndMessage()
    {
        WriteLog("20260808", Line("WRN", "SmtpRelay", "Rejected 10.0.0.1: not whitelisted"));

        var entry = LogFileReader.Read(_dir, 100, null).Entries.Single();

        entry.Level.Should().Be("Warning");
        entry.Component.Should().Be("SmtpRelay");
        entry.Message.Should().Be("Rejected 10.0.0.1: not whitelisted");
    }

    [Fact]
    public void Read_ContinuationLines_AreAppendedToThePrecedingEntry()
    {
        WriteLog("20260808",
            Line("ERR", "QueueProcessor", "Delivery failed"),
            "System.Net.Http.HttpRequestException: timeout",
            "   at GraphApiClient.SendAsync()");

        var entries = LogFileReader.Read(_dir, 100, null).Entries;

        entries.Should().HaveCount(1, "a stack trace belongs to its log entry, not next to it");
        entries[0].RawLine.Should().Contain("at GraphApiClient.SendAsync()");
    }

    [Fact]
    public void Read_StackTraceContent_IsSearchable()
    {
        WriteLog("20260808",
            Line("ERR", "QueueProcessor", "Delivery failed"),
            "   at GraphApiClient.SendAsync()");

        var result = LogFileReader.Read(_dir, 100, e => e.Matches("SendAsync"));

        result.Entries.Should().HaveCount(1,
            "the stack trace is only reachable through the entry that carries it");
    }

    // ── Ordering across files ────────────────────────────────────────────────

    [Fact]
    public void Read_MultipleFiles_ReturnsNewestFileAndNewestLineFirst()
    {
        WriteLog("20260807", Line("INF", "Startup", "older-1"), Line("INF", "Startup", "older-2"));
        WriteLog("20260808", Line("INF", "Startup", "newer-1"), Line("INF", "Startup", "newer-2"));

        var messages = LogFileReader.Read(_dir, 100, null).Entries.Select(e => e.Message).ToList();

        messages.Should().Equal("newer-2", "newer-1", "older-2", "older-1");
    }

    [Fact]
    public void Read_ReachesFilesBeyondTheNewestTwo()
    {
        for (var day = 1; day <= 5; day++)
            WriteLog($"2026080{day}", Line("INF", "Startup", $"day-{day}"));

        var messages = LogFileReader.Read(_dir, 100, null).Entries.Select(e => e.Message).ToList();

        messages.Should().Contain("day-1",
            "the retained history is seven files — the page used to stop after two");
    }

    // ── Paging ───────────────────────────────────────────────────────────────

    [Fact]
    public void Read_LimitReached_ReportsHasMore()
    {
        WriteLog("20260808", Enumerable.Range(1, 10).Select(i => Line("INF", "Startup", $"m{i}")).ToArray());

        var result = LogFileReader.Read(_dir, 4, null);

        result.Entries.Should().HaveCount(4);
        result.HasMore.Should().BeTrue();
    }

    [Fact]
    public void Read_WholeLogFitsInTheLimit_ReportsNoMore()
    {
        WriteLog("20260808", Line("INF", "Startup", "only-one"));

        var result = LogFileReader.Read(_dir, 100, null);

        result.HasMore.Should().BeFalse("offering another page when the log is exhausted misleads");
    }

    [Fact]
    public void Read_RaisedLimit_ReturnsTheEarlierEntriesToo()
    {
        WriteLog("20260808", Enumerable.Range(1, 10).Select(i => Line("INF", "Startup", $"m{i}")).ToArray());

        var page2 = LogFileReader.Read(_dir, 8, null).Entries.Select(e => e.Message).ToList();

        page2.Should().HaveCount(8).And.Contain("m3", "\"load more\" must reach further back, not reshuffle");
    }

    // ── Filtering ────────────────────────────────────────────────────────────

    [Fact]
    public void Read_Predicate_SearchesBeyondOnePageOfEntries()
    {
        var lines = Enumerable.Range(1, 50).Select(i => Line("INF", "Startup", $"noise-{i}")).ToList();
        lines.Insert(0, Line("ERR", "QueueProcessor", "the-needle"));   // oldest line in the file
        WriteLog("20260808", [.. lines]);

        var result = LogFileReader.Read(_dir, 5, e => e.Matches("needle"));

        result.Entries.Should().ContainSingle(e => e.Message == "the-needle",
            "a search that only looked at the newest page is the bug this replaced");
    }

    [Fact]
    public void Read_Predicate_StillCollectsEveryComponentItScanned()
    {
        WriteLog("20260808",
            Line("INF", "SmtpRelay", "a"),
            Line("INF", "QueueProcessor", "b"),
            Line("INF", "GraphApi", "c"));

        var result = LogFileReader.Read(_dir, 100, e => e.Component == "GraphApi");

        result.Entries.Should().HaveCount(1);
        result.Components.Should().BeEquivalentTo(["SmtpRelay", "QueueProcessor", "GraphApi"],
            "the component dropdown is built from this — filtering to one component must not " +
            "remove the other choices and trap the user");
    }

    // ── Scan cap ─────────────────────────────────────────────────────────────

    [Fact]
    public void Read_FilteredScanExceedingTheCap_StopsAndSaysSo()
    {
        WriteLog("20260808", Enumerable.Range(1, 50).Select(i => Line("INF", "Startup", $"m{i}")).ToArray());

        var result = LogFileReader.Read(_dir, 100, e => e.Matches("no-such-term"), maxScan: 10);

        result.Entries.Should().BeEmpty();
        result.ScanCapped.Should().BeTrue();
        result.Scanned.Should().Be(10);
    }

    [Fact]
    public void Read_UnfilteredRead_IsNotSubjectToTheScanCap()
    {
        WriteLog("20260808", Enumerable.Range(1, 50).Select(i => Line("INF", "Startup", $"m{i}")).ToArray());

        var result = LogFileReader.Read(_dir, 40, null, maxScan: 10);

        result.Entries.Should().HaveCount(40, "the page limit already bounds an unfiltered read");
        result.ScanCapped.Should().BeFalse();
    }

    [Fact]
    public void Read_CompletedFilteredScan_ReportsTheWholeLogAsScanned()
    {
        WriteLog("20260808", Enumerable.Range(1, 12).Select(i => Line("INF", "Startup", $"m{i}")).ToArray());

        var result = LogFileReader.Read(_dir, 100, e => e.Matches("m7"));

        result.Scanned.Should().Be(12, "the counter reports matches out of what was examined");
        result.ScanCapped.Should().BeFalse();
    }

    // ── Matches predicate ────────────────────────────────────────────────────

    [Fact]
    public void Matches_EmptyTerm_MatchesEverything()
    {
        new LogEntry("t", "Information", "SmtpRelay", "msg", "raw").Matches("  ").Should().BeTrue();
    }

    [Theory]
    [InlineData("MSG")]          // message, case-insensitive
    [InlineData("smtprelay")]    // component, case-insensitive
    public void Matches_TermInMessageOrComponent_ReturnsTrue(string term)
    {
        new LogEntry("t", "Information", "SmtpRelay", "msg", "raw").Matches(term).Should().BeTrue();
    }

    [Fact]
    public void Matches_TermNowhere_ReturnsFalse()
    {
        new LogEntry("t", "Information", "SmtpRelay", "msg", "raw").Matches("fabrikam").Should().BeFalse();
    }
}
