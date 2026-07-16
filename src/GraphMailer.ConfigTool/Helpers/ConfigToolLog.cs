using System.Collections.Concurrent;
using System.IO;
using GraphMailer.Service.Infrastructure;
using Serilog;
using Serilog.Core;

namespace GraphMailer.ConfigTool.Helpers;

/// <summary>
/// Process-wide diagnostic logger for the ConfigTool. Writes rolling
/// <c>configtool-*.log</c> files next to the service logs so both handled UI
/// errors and crashes are persisted with full stack traces.
/// Logging must never break the UI: when the log directory is unavailable the
/// logger degrades to a silent no-op (crashes are still covered by the
/// crash-file fallback in <see cref="App"/>).
/// </summary>
internal static class ConfigToolLog
{
    private static readonly Lazy<Logger?> _instance = new(() => CreateLogger(AppPaths.LogsDir));

    /// <summary>Logs a handled exception (shown to the user as a short message) with its stack trace.</summary>
    internal static void Error(string source, Exception ex, string? context = null)
        => _instance.Value?.Error(ex, "[{Source:l}] {Context:l}", source, context ?? ex.Message);

    /// <summary>
    /// Like <see cref="Error"/>, but suppresses repeats: logs only when the exception
    /// (type + message) for this source/context differs from the previously logged one.
    /// For periodic checks that would otherwise repeat the same trace every few seconds.
    /// </summary>
    internal static void ErrorOnChange(string source, Exception ex, string? context = null)
    {
        if (IsNewSignature(source + "|" + context, ex.GetType().FullName + "|" + ex.Message))
            Error(source, ex, context);
    }

    /// <summary>True when this signature differs from the last one recorded for the key.</summary>
    internal static bool IsNewSignature(string key, string signature)
    {
        if (_lastSignaturePerSource.TryGetValue(key, out var previous) && previous == signature)
            return false;
        _lastSignaturePerSource[key] = signature;
        return true;
    }

    private static readonly ConcurrentDictionary<string, string> _lastSignaturePerSource = new();

    /// <summary>Logs an unhandled exception caught by one of the crash hooks.</summary>
    internal static void Fatal(string source, Exception ex, string? context = null)
        => _instance.Value?.Fatal(ex, "[{Source:l}] {Context:l}", source, context ?? ex.Message);

    /// <summary>Flushes and closes the log file; call once on application exit.</summary>
    internal static void Flush()
    {
        if (_instance.IsValueCreated)
            _instance.Value?.Dispose();
    }

    /// <summary>
    /// Creates the file logger, or returns null when the directory cannot be
    /// created/written (diagnostics then degrade to a no-op).
    /// </summary>
    internal static Logger? CreateLogger(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            return new LoggerConfiguration()
                .WriteTo.File(
                    Path.Combine(directory, "configtool-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    fileSizeLimitBytes: 10_485_760,
                    rollOnFileSizeLimit: true)
                .CreateLogger();
        }
        catch
        {
            return null;
        }
    }
}
