using GraphMailer.ConfigTool.Helpers;

namespace GraphMailer.Tests.Unit.ConfigTool;

/// <summary>
/// The ConfigTool diagnostic log is the only persisted trace of errors the UI
/// shows as one-line messages — the log file must contain the full exception
/// (type + stack trace), and an unusable log directory must degrade to a silent
/// no-op instead of breaking the UI.
/// </summary>
public sealed class ConfigToolLogTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "configtoollog-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static Exception Throw()
    {
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (Exception ex)
        {
            return ex;   // caught so the exception carries a real stack trace
        }
    }

    [Fact]
    public void CreateLogger_ValidDirectory_WritesExceptionTypeAndStackTrace()
    {
        using (var logger = ConfigToolLog.CreateLogger(_tempDir))
        {
            logger.Should().NotBeNull();
            logger!.Error(Throw(), "[{Source:l}] {Context:l}", "TestPage", "Something failed");
        }

        var files = Directory.GetFiles(_tempDir, "configtool-*.log");
        files.Should().HaveCount(1);
        var content = File.ReadAllText(files[0]);
        content.Should().Contain("[TestPage] Something failed");
        content.Should().Contain(nameof(InvalidOperationException));
        content.Should().Contain(nameof(Throw), "the stack trace must name the throwing frame");
    }

    [Fact]
    public void CreateLogger_DirectoryPathIsAFile_ReturnsNullInsteadOfThrowing()
    {
        Directory.CreateDirectory(_tempDir);
        var blockingFile = Path.Combine(_tempDir, "not-a-directory");
        File.WriteAllText(blockingFile, "blocks directory creation");

        var logger = ConfigToolLog.CreateLogger(blockingFile);

        logger.Should().BeNull("an unusable log directory must degrade to a no-op, never break the UI");
    }

    [Fact]
    public void IsNewSignature_RepeatedAndChangedSignatures_SuppressesOnlyExactRepeats()
    {
        // Periodic checks (5 s timers) call ErrorOnChange — a recurring identical failure
        // must be logged once, but a *different* failure for the same source must not be
        // swallowed, and the same failure must reappear after the error changed in between.
        var key = "test|" + Guid.NewGuid().ToString("N");   // unique key: the dedupe map is process-wide

        ConfigToolLog.IsNewSignature(key, "IOException|disk full").Should().BeTrue("first occurrence");
        ConfigToolLog.IsNewSignature(key, "IOException|disk full").Should().BeFalse("exact repeat");
        ConfigToolLog.IsNewSignature(key, "IOException|access denied").Should().BeTrue("different failure");
        ConfigToolLog.IsNewSignature(key, "IOException|disk full").Should().BeTrue("recurrence after a change");
    }
}
