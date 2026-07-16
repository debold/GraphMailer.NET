using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GraphMailer.ConfigTool.Helpers;
using GraphMailer.Service.Infrastructure;

namespace GraphMailer.ConfigTool;

public partial class App : Application
{
    private static readonly string CrashLogPath = ResolveCrashLogPath();

    /// <summary>
    /// Crash info belongs next to the service logs where operators already look;
    /// the install directory is only the fallback when the logs directory cannot
    /// be created (e.g. non-elevated dev run against the real ProgramData tree).
    /// </summary>
    private static string ResolveCrashLogPath()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            return Path.Combine(AppPaths.LogsDir, "configtool-crash.log");
        }
        catch
        {
            return Path.Combine(AppContext.BaseDirectory, "configtool-crash.log");
        }
    }

    private SingleInstanceGuard? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Machine-wide single-instance lock: two ConfigTool instances would race
        // on graphmailer.json writes and the file-based service IPC.
        _singleInstance = new SingleInstanceGuard("GraphMailer.ConfigTool");
        if (!_singleInstance.IsPrimaryInstance)
        {
            ActivateExistingInstance();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // Catch unhandled exceptions on the UI thread
        DispatcherUnhandledException += (_, ex) =>
        {
            ConfigToolLog.Fatal("App", ex.Exception, "DispatcherUnhandledException");
            WriteCrashLog("DispatcherUnhandledException", ex.Exception);

            // If the main window is already up, keep the app alive so the user can read
            // the message and keep working. But a crash DURING STARTUP (before the window
            // is loaded) would otherwise leave a windowless zombie process holding the
            // single-instance mutex — blocking every restart until it is killed by hand.
            // In that case, exit cleanly so OnExit releases the mutex.
            var startedUp = MainWindow is { IsLoaded: true };

            MessageBox.Show(
                $"Unexpected error:\n{ex.Exception.GetType().Name}: {ex.Exception.Message}\n\nDetails written to:\n{CrashLogPath}" +
                (startedUp ? "" : "\n\nThe application could not start and will now close."),
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);

            ex.Handled = true;
            if (!startedUp)
                Shutdown(1);   // triggers OnExit → releases the single-instance lock
        };

        // Catch unhandled exceptions on background threads
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            if (ex.ExceptionObject is Exception exc)
            {
                ConfigToolLog.Fatal("App", exc, "AppDomain.UnhandledException");
                WriteCrashLog("AppDomain.UnhandledException", exc);
            }
        };

        // Catch exceptions from faulted Tasks nobody awaited (fire-and-forget UI
        // handlers) — otherwise they vanish when the Task is garbage-collected.
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            ConfigToolLog.Error("App", ex.Exception, "TaskScheduler.UnobservedTaskException");
            WriteCrashLog("TaskScheduler.UnobservedTaskException", ex.Exception);
            ex.SetObserved();
        };

        // Select all text in every TextBox when it receives keyboard focus (Tab or click)
        EventManager.RegisterClassHandler(
            typeof(TextBox),
            UIElement.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler((s, _) => ((TextBox)s).SelectAll()));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ConfigToolLog.Flush();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Brings the window of the already running instance to the foreground.
    /// Falls back to a message box when the window cannot be found
    /// (e.g. the other instance runs in a different RDP session).
    /// </summary>
    private static void ActivateExistingInstance()
    {
        try
        {
            var other = Process.GetProcessesByName("GraphMailer.ConfigTool")
                .FirstOrDefault(p => p.Id != Environment.ProcessId && p.MainWindowHandle != IntPtr.Zero);
            if (other is not null)
            {
                if (NativeMethods.IsIconic(other.MainWindowHandle))
                    NativeMethods.ShowWindow(other.MainWindowHandle, NativeMethods.SW_RESTORE);
                NativeMethods.SetForegroundWindow(other.MainWindowHandle);
                return;
            }
        }
        catch
        {
            // fall through to the message box
        }

        MessageBox.Show(
            "GraphMailer Config Tool is already running.",
            "GraphMailer Config Tool", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static class NativeMethods
    {
        public const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);
    }

    private static void WriteCrashLog(string source, Exception ex)
    {
        try
        {
            var lines = new List<string>
            {
                $"=== CRASH {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] ===",
                $"Type:    {ex.GetType().FullName}",
                $"Message: {ex.Message}",
                "StackTrace:",
                ex.StackTrace ?? "(none)",
            };

            var inner = ex.InnerException;
            int depth = 1;
            while (inner is not null && depth <= 5)
            {
                lines.Add($"--- InnerException ({depth}) ---");
                lines.Add($"Type:    {inner.GetType().FullName}");
                lines.Add($"Message: {inner.Message}");
                lines.Add(inner.StackTrace ?? "(none)");
                inner = inner.InnerException;
                depth++;
            }

            lines.Add(string.Empty);
            File.AppendAllLines(CrashLogPath, lines);
        }
        catch { /* never crash the crash logger */ }
    }
}

