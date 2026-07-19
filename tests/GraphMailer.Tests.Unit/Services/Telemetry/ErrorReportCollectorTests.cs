using FluentAssertions;
using GraphMailer.Service.Services.Telemetry;

namespace GraphMailer.Tests.Unit.Services.Telemetry;

public sealed class ErrorReportCollectorTests
{
    private readonly ErrorReportCollector _sut = new();

    private static void RecordSample(ErrorReportCollector collector, string template = "[Queue] Delivery failed for {MessageId}")
        => collector.Record(template, "Queue", "System.InvalidOperationException", "   at GraphMailer.Foo.Bar()", null, null);

    // ── Fingerprint dedupe & aggregation ──────────────────────────────────

    [Fact]
    public void Record_SameErrorTwice_AggregatesIntoOneReportWithCount2()
    {
        RecordSample(_sut);
        RecordSample(_sut);

        var (reports, overflow) = _sut.Drain();

        reports.Should().ContainSingle().Which.Count.Should().Be(2);
        overflow.Should().Be(0);
    }

    [Fact]
    public void Record_DifferentTemplates_ProduceSeparateReports()
    {
        RecordSample(_sut, "[Queue] Delivery failed for {MessageId}");
        RecordSample(_sut, "[Metrics] Failed to record {EventType} for {MessageId}");

        _sut.Drain().Reports.Should().HaveCount(2);
    }

    [Fact]
    public void ComputeFingerprint_SameInputs_IsStable_DifferentInputs_Differ()
    {
        var a1 = ErrorReportCollector.ComputeFingerprint("[X] {Y}", "T", "   at Foo.Bar()");
        var a2 = ErrorReportCollector.ComputeFingerprint("[X] {Y}", "T", "   at Foo.Bar()");
        var b = ErrorReportCollector.ComputeFingerprint("[X] {Y}", "T", "   at Other.Site()");

        a1.Should().Be(a2, "the same error site must group across installs");
        a1.Should().NotBe(b, "distinct call sites of a shared template must stay separate");
    }

    // ── Bounded collector (log-storm protection) ──────────────────────────

    [Fact]
    public void Record_BeyondCap_OnlyIncrementsOverflow()
    {
        for (var i = 0; i < ErrorReportCollector.MaxDistinctReports + 7; i++)
            RecordSample(_sut, $"[Test] Error template number {i}");

        var (reports, overflow) = _sut.Drain();

        reports.Should().HaveCount(ErrorReportCollector.MaxDistinctReports);
        overflow.Should().Be(7);
    }

    [Fact]
    public void Record_KnownFingerprintAtCap_StillAggregates()
    {
        for (var i = 0; i < ErrorReportCollector.MaxDistinctReports; i++)
            RecordSample(_sut, $"[Test] Error template number {i}");

        RecordSample(_sut, "[Test] Error template number 0");   // known — must not overflow

        var (reports, overflow) = _sut.Drain();
        overflow.Should().Be(0);
        reports.Should().Contain(r => r.MessageTemplate == "[Test] Error template number 0" && r.Count == 2);
    }

    // ── Drain / Restore (failed-transmission retry) ───────────────────────

    [Fact]
    public void Drain_ClearsCollector()
    {
        RecordSample(_sut);

        _sut.Drain();

        _sut.Drain().Reports.Should().BeEmpty();
    }

    [Fact]
    public void Restore_AfterFailedFlush_MergesWithNewOccurrences()
    {
        RecordSample(_sut);
        var (drained, overflow) = _sut.Drain();

        RecordSample(_sut);              // same error happened again while sending failed
        _sut.Restore(drained, overflow);

        var report = _sut.Drain().Reports.Should().ContainSingle().Subject;
        report.Count.Should().Be(2, "the drained count must merge with the new occurrence");
    }

    // ── PII guarantee: reports carry only what was recorded ───────────────

    [Fact]
    public void Record_ReportContainsTemplateAndTypesOnly_NoRenderedValues()
    {
        _sut.Record(
            "[Queue] Delivery failed for {MessageId} from {From}",
            "Queue",
            "GraphMailer.Service.Services.GraphDeliveryException",
            "   at GraphMailer.Service.Services.QueueProcessor.DeliverAsync()",
            "ErrorInvalidRecipients",
            404);

        var report = _sut.Drain().Reports.Should().ContainSingle().Subject;
        report.MessageTemplate.Should().Contain("{MessageId}", "templates keep placeholders, never values");
        report.Component.Should().Be("Queue");
        report.ExceptionType.Should().NotContain(":", "type names only, no exception messages");
        report.GraphErrorCode.Should().Be("ErrorInvalidRecipients");
        report.HttpStatus.Should().Be(404);
    }
}
