using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using GraphMailer.ConfigTool.Helpers;
using GraphMailer.ConfigTool.Services;

namespace GraphMailer.ConfigTool.Views;

// ── Step view-model ────────────────────────────────────────────────────────────

internal sealed class SetupStepViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private SetupStepState _state = SetupStepState.Pending;
    private string? _detail;
    private string? _loginUrl;
    private string? _countdown;
    private string? _decisionText;
    private bool _decisionPending;

    internal TaskCompletionSource<bool>? PendingDecision { get; set; }

    public string Label { get; init; } = string.Empty;

    public SetupStepState State
    {
        get => _state;
        set
        {
            _state = value;
            Notify();
            Notify(nameof(Icon)); Notify(nameof(IconBrush));
            Notify(nameof(LabelBrush)); Notify(nameof(ItemBackground));
            Notify(nameof(ItemBorderBrush)); Notify(nameof(ExpandedVisibility));
            Notify(nameof(SummaryVisibility)); Notify(nameof(ExpandedDetailVisibility));
            Notify(nameof(CountdownVisibility)); Notify(nameof(LoginUrlPanelVisibility));
        }
    }

    public string? Detail
    {
        get => _detail;
        set { _detail = value; Notify(); Notify(nameof(SummaryVisibility)); Notify(nameof(ExpandedDetailVisibility)); }
    }

    public string? LoginUrl
    {
        get => _loginUrl;
        set { _loginUrl = value; Notify(); Notify(nameof(LoginUrlPanelVisibility)); }
    }

    public string? Countdown
    {
        get => _countdown;
        set { _countdown = value; Notify(); Notify(nameof(CountdownVisibility)); }
    }

    public string? DecisionText
    {
        get => _decisionText;
        set { _decisionText = value; Notify(); }
    }

    public bool DecisionPending
    {
        get => _decisionPending;
        set { _decisionPending = value; Notify(); Notify(nameof(DecisionVisibility)); }
    }

    // ── Icon ──────────────────────────────────────────────────────────────────

    public string Icon => _state switch
    {
        SetupStepState.Pending => "○",
        SetupStepState.Running => "↻",
        SetupStepState.Done => "✔",
        SetupStepState.Skipped => "–",
        SetupStepState.Error => "✗",
        _ => "○",
    };

    public Brush IconBrush => _state switch
    {
        SetupStepState.Running => new SolidColorBrush(Color.FromRgb(0x3B, 0x9F, 0xE8)),
        SetupStepState.Done => new SolidColorBrush(Color.FromRgb(0x6C, 0xCB, 0x5F)),
        SetupStepState.Skipped => new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
        SetupStepState.Error => new SolidColorBrush(Color.FromRgb(0xE0, 0x60, 0x60)),
        _ => new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
    };

    public Brush LabelBrush => _state switch
    {
        SetupStepState.Pending => new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
        SetupStepState.Skipped => new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
        _ => new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
    };

    // ── Card styling ──────────────────────────────────────────────────────────

    public Brush ItemBackground => _state switch
    {
        SetupStepState.Running => System.Windows.Media.Brushes.White,
        SetupStepState.Error => new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xF4)),
        _ => new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA)),
    };

    public Brush ItemBorderBrush => _state switch
    {
        SetupStepState.Running => new SolidColorBrush(Color.FromRgb(0x3B, 0x9F, 0xE8)),
        SetupStepState.Done => new SolidColorBrush(Color.FromRgb(0x6C, 0xCB, 0x5F)),
        SetupStepState.Error => new SolidColorBrush(Color.FromRgb(0xE0, 0x60, 0x60)),
        SetupStepState.Skipped => new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
        _ => new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
    };

    // ── Visibility ────────────────────────────────────────────────────────────

    public Visibility ExpandedVisibility =>
        _state is SetupStepState.Running or SetupStepState.Error
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SummaryVisibility =>
        !string.IsNullOrWhiteSpace(_detail) &&
        _state is SetupStepState.Done or SetupStepState.Skipped
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ExpandedDetailVisibility =>
        !string.IsNullOrWhiteSpace(_detail) &&
        _state is SetupStepState.Running or SetupStepState.Error
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CountdownVisibility =>
        _state == SetupStepState.Running && !string.IsNullOrEmpty(_countdown)
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LoginUrlPanelVisibility =>
        _state == SetupStepState.Running && !string.IsNullOrWhiteSpace(_loginUrl)
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DecisionVisibility =>
        _state == SetupStepState.Running && _decisionPending
            ? Visibility.Visible : Visibility.Collapsed;

    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ── Window ─────────────────────────────────────────────────────────────────────

public partial class EntraSetupProgressWindow : Window
{
    private readonly string _appName;

    private readonly CancellationTokenSource _cts = new();
    private readonly ObservableCollection<SetupStepViewModel> _steps = [];
    private readonly DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _countdownDeadline;
    private bool _operationRunning;

    /// <summary>Populated on successful completion; null if cancelled or failed.</summary>
    internal EntraSetupService.Result? SetupResult { get; private set; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public EntraSetupProgressWindow(string appName)
    {
        _appName = appName;
        InitializeComponent();
        SubtitleText.Text =
            "Sign in with an account that holds Application Administrator " +
            "or Global Administrator. The wizard will check for an existing " +
            "registration and reuse it if found.";
        foreach (var label in SetupSteps.Labels)
            _steps.Add(new SetupStepViewModel { Label = label });
        StepsList.ItemsSource = _steps;
        _countdownTimer.Tick += CountdownTimer_Tick;
    }

    // ── Window events ─────────────────────────────────────────────────────────

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _operationRunning = true;
        try
        {
            SetupResult = await EntraSetupService.RunAsync(
                _appName, Report, OnLoginUrl, OnCertDecision, _cts.Token);

            ShowResult("✔  Completed successfully.", isError: false);
        }
        catch (OperationCanceledException)
        {
            MarkRunningStepsAsCancelled();
            ShowResult("Cancelled.", isError: false);
        }
        catch (Exception ex)
        {
            ConfigToolLog.Error("EntraSetup", ex, "Entra setup wizard failed");
            MarkRunningStepsAsError(ex.Message);
            ShowResult($"⚠  {ex.Message}", isError: true);
        }
        finally
        {
            _operationRunning = false;
            _countdownTimer.Stop();
            _cts.Dispose();
            CancelButton.Visibility = Visibility.Collapsed;
            CloseButton.Visibility = Visibility.Visible;
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_operationRunning && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            e.Cancel = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        CancelButton.IsEnabled = false;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ── Countdown ─────────────────────────────────────────────────────────────

    private void CountdownTimer_Tick(object? sender, EventArgs e)
    {
        if (_steps.Count == 0) return;
        var remaining = _countdownDeadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            _steps[0].Countdown = null;
            _countdownTimer.Stop();
        }
        else
        {
            var m = (int)remaining.TotalMinutes;
            var s = remaining.Seconds;
            _steps[0].Countdown = $"⏱  {m}:{s:D2} remaining";
        }
    }

    // ── Login URL callback ────────────────────────────────────────────────────

    private void OnLoginUrl(Uri uri)
    {
        Dispatcher.Invoke(() =>
        {
            if (_steps.Count > 0)
                _steps[0].LoginUrl = uri.AbsoluteUri;
        });
    }

    // ── Certificate decision callback ─────────────────────────────────────────

    private Task<bool> OnCertDecision(string subject, string thumbprint, int daysRemaining, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>();

        // Cancel the TCS if the user presses Cancel.
        ct.Register(() => tcs.TrySetCanceled());

        Dispatcher.Invoke(() =>
        {
            var certStep = _steps.Count > SetupSteps.GenerateCert ? _steps[SetupSteps.GenerateCert] : null;
            if (certStep is null) { tcs.TrySetResult(false); return; }

            certStep.DecisionText = $"An existing certificate is still valid for {daysRemaining} days:\n" +
                                       $"{subject}\nThumbprint: {thumbprint}\n\n" +
                                       "Keep the existing certificate, or generate a new one?";
            certStep.PendingDecision = tcs;
            certStep.DecisionPending = true;
        });

        return tcs.Task;
    }

    // ── DataTemplate button handlers ──────────────────────────────────────────

    private void OpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string url && !string.IsNullOrEmpty(url))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string url && !string.IsNullOrEmpty(url))
            Clipboard.SetText(url);
    }

    private void KeepCert_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is SetupStepViewModel vm)
        {
            vm.DecisionPending = false;
            vm.PendingDecision?.TrySetResult(false);
        }
    }

    private void ReplaceCert_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is SetupStepViewModel vm)
        {
            vm.DecisionPending = false;
            vm.PendingDecision?.TrySetResult(true);
        }
    }

    // ── Step reporting ────────────────────────────────────────────────────────

    private void Report(int stepIndex, SetupStepState state, string? detail = null)
    {
        Dispatcher.Invoke(() =>
        {
            if (stepIndex < 0 || stepIndex >= _steps.Count) return;
            var step = _steps[stepIndex];
            step.State = state;
            step.Detail = detail;

            if (stepIndex == 0)
            {
                if (state == SetupStepState.Running)
                {
                    _countdownDeadline = DateTime.UtcNow.AddMinutes(EntraSetupService.SignInTimeoutMinutes);
                    _countdownTimer.Start();
                }
                else
                {
                    _countdownTimer.Stop();
                    step.Countdown = null;
                }
            }
        });
    }

    private void MarkRunningStepsAsCancelled()
    {
        foreach (var step in _steps)
            if (step.State == SetupStepState.Running)
            {
                step.DecisionPending = false;
                step.PendingDecision?.TrySetCanceled();
                step.State = SetupStepState.Error;
                step.Detail = "Cancelled.";
            }
    }

    private void MarkRunningStepsAsError(string message)
    {
        foreach (var step in _steps)
            if (step.State == SetupStepState.Running)
            {
                step.DecisionPending = false;
                step.PendingDecision?.TrySetCanceled();
                step.State = SetupStepState.Error;
                step.Detail = message;
            }
    }

    private void ShowResult(string message, bool isError)
    {
        ResultText.Text = message;
        ResultBox.Style = isError
            ? (Style)FindResource("WarnBox")
            : (Style)FindResource("OkBox");
        ResultBox.Visibility = Visibility.Visible;
    }
}