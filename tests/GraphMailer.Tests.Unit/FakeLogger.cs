using Microsoft.Extensions.Logging;

namespace GraphMailer.Tests.Unit;

/// <summary>
/// Captures log entries written by a component under test.
///
/// Usage:
///   var logger = new FakeLogger&lt;MyService&gt;();
///   var sut = new MyService(logger);
///   sut.DoSomething();
///   logger.Should().ContainSingle(LogLevel.Warning, "expected text");
///
/// Guidelines – when to use log tests:
///   YES: security-critical warnings/errors where the log IS the operator signal
///        (e.g. TLS cert missing, auth failure, IP blocked).
///   NO:  debug/info lines that exist only for operational tracing; those tests
///        become brittle and tie tests to message wording rather than behaviour.
///
/// If a logged condition has a measurable behavioural side-effect (e.g. a method
/// returns false, a connection is rejected), prefer testing that behaviour directly
/// and skip the log assertion.
/// </summary>
internal sealed class FakeLogger<T> : ILogger<T>
{
    public record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries => _entries;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    // -------------------------------------------------------------------------
    // Assertion helpers
    // -------------------------------------------------------------------------

    public bool HasEntry(LogLevel level, string containsText) =>
        _entries.Any(e => e.Level == level &&
                          e.Message.Contains(containsText, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<LogEntry> EntriesAt(LogLevel level) =>
        _entries.Where(e => e.Level == level).ToList();
}
