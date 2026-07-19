using FluentAssertions;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services.Telemetry;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Models.ODataErrors;
using NSubstitute;
using Serilog.Events;
using Serilog.Parsing;

namespace GraphMailer.Tests.Unit.Services.Telemetry;

public sealed class TelemetrySinkTests
{
    private readonly ErrorReportCollector _collector = new();
    private bool _enabled = true;

    private TelemetrySink CreateSink()
    {
        var options = Substitute.For<IOptionsMonitor<TelemetryOptions>>();
        options.CurrentValue.Returns(_ => new TelemetryOptions { Enabled = _enabled });
        return new TelemetrySink(_collector, options);
    }

    private static LogEvent CreateEvent(
        LogEventLevel level,
        string template,
        Exception? exception = null,
        params (string Name, object Value)[] properties)
        => new(
            DateTimeOffset.UtcNow,
            level,
            exception,
            new MessageTemplateParser().Parse(template),
            properties.Select(p => new LogEventProperty(p.Name, new ScalarValue(p.Value))));

    // ── Level + opt-in gates ──────────────────────────────────────────────

    [Theory]
    [InlineData(LogEventLevel.Warning)]
    [InlineData(LogEventLevel.Information)]
    [InlineData(LogEventLevel.Debug)]
    public void Emit_BelowError_IsIgnored(LogEventLevel level)
    {
        CreateSink().Emit(CreateEvent(level, "[Test] Something happened"));

        _collector.Drain().Reports.Should().BeEmpty();
    }

    [Fact]
    public void Emit_TelemetryDisabled_RecordsNothing()
    {
        _enabled = false;

        CreateSink().Emit(CreateEvent(LogEventLevel.Error, "[Test] Broken"));

        _collector.Drain().Reports.Should().BeEmpty("while disabled no data may even be collected");
    }

    [Theory]
    [InlineData(LogEventLevel.Error)]
    [InlineData(LogEventLevel.Fatal)]
    public void Emit_ErrorOrFatal_IsRecorded(LogEventLevel level)
    {
        CreateSink().Emit(CreateEvent(level, "[Test] Broken"));

        _collector.Drain().Reports.Should().ContainSingle();
    }

    // ── PII guarantee: template, not rendered message ─────────────────────

    [Fact]
    public void Emit_CapturesTemplate_NeverRenderedPropertyValues()
    {
        var sink = CreateSink();

        sink.Emit(CreateEvent(
            LogEventLevel.Error,
            "[Queue] Delivery failed for {MessageId} from {From}",
            exception: null,
            ("MessageId", "abc123"), ("From", "ceo@customer-corp.com")));

        var report = _collector.Drain().Reports.Should().ContainSingle().Subject;
        report.MessageTemplate.Should().Be("[Queue] Delivery failed for {MessageId} from {From}");
        report.MessageTemplate.Should().NotContain("customer-corp.com",
            "rendered values (addresses, hosts) must never reach telemetry");
    }

    [Fact]
    public void Emit_WithException_KeepsTypeAndStack_DropsMessage()
    {
        Exception thrown;
        try { throw new InvalidOperationException("secret detail: user@customer-corp.com"); }
        catch (Exception ex) { thrown = ex; }

        CreateSink().Emit(CreateEvent(LogEventLevel.Error, "[Test] Broken", thrown));

        var report = _collector.Drain().Reports.Should().ContainSingle().Subject;
        report.ExceptionType.Should().Be("System.InvalidOperationException");
        report.StackTrace.Should().NotBeNull().And.NotContain("customer-corp.com",
            "exception messages are dropped — only frames are kept");
    }

    // ── Structured Graph error extraction ─────────────────────────────────

    [Fact]
    public void Emit_ODataErrorInInnerChain_ExtractsCodeAndStatus()
    {
        var odata = new ODataError
        {
            ResponseStatusCode = 404,
            Error = new MainError { Code = "ErrorInvalidUser" },
        };
        var wrapped = new InvalidOperationException("outer", odata);

        CreateSink().Emit(CreateEvent(LogEventLevel.Error, "[GraphApi] Send failed", wrapped));

        var report = _collector.Drain().Reports.Should().ContainSingle().Subject;
        report.GraphErrorCode.Should().Be("ErrorInvalidUser");
        report.HttpStatus.Should().Be(404);
        report.ExceptionType.Should().Contain("InvalidOperationException").And.Contain("ODataError");
    }

    // ── Helper coverage ───────────────────────────────────────────────────

    [Theory]
    [InlineData("[SmtpRelay] Listener failed", "SmtpRelay")]
    [InlineData("no prefix here", "unknown")]
    [InlineData("[] empty", "unknown")]
    public void ExtractComponent_ParsesLoggingConventionPrefix(string template, string expected)
        => TelemetrySink.ExtractComponent(template).Should().Be(expected);
}
