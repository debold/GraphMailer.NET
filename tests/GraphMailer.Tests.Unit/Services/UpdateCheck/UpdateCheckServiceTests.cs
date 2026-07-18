using FluentAssertions;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services;
using GraphMailer.Service.Services.UpdateCheck;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GraphMailer.Tests.Unit.Services.UpdateCheck;

public sealed class UpdateCheckServiceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "gm-updatecheck-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _statusPath;
    private readonly string _requestPath;

    private readonly IUpdateChecker _checker = Substitute.For<IUpdateChecker>();
    private readonly IAdminNotificationService _notifications = Substitute.For<IAdminNotificationService>();

    public UpdateCheckServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _statusPath = Path.Combine(_dir, "update-status.json");
        _requestPath = Path.Combine(_dir, "update-check.request");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private UpdateCheckService CreateService()
    {
        var options = Substitute.For<IOptionsMonitor<UpdateCheckOptions>>();
        options.CurrentValue.Returns(new UpdateCheckOptions { Enabled = true });
        return new UpdateCheckService(_checker, options, _notifications, NullLogger<UpdateCheckService>.Instance)
        {
            StatusPath = _statusPath,
            RequestPath = _requestPath,
        };
    }

    private static UpdateCheckResult SuccessResult(bool updateAvailable, string latest = "1.3.0.210")
        => new(true, "1.2.0.196",
            LatestVersion: latest,
            UpdateAvailable: updateAvailable,
            ReleaseUrl: $"https://github.com/debold/GraphMailer.NET/releases/tag/v{latest}",
            ReleaseName: $"GraphMailer v{latest}",
            PublishedUtc: new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc));

    // ── Due-time logic (weekly cadence persisted across restarts) ─────────

    [Fact]
    public void IsCheckDue_NoStatusFile_IsDue()
        => CreateService().IsCheckDue(DateTime.UtcNow).Should().BeTrue("the first check runs right after enabling");

    [Fact]
    public void IsCheckDue_NextCheckInFuture_IsNotDue()
    {
        new UpdateCheckStatus { NextCheckUtc = DateTime.UtcNow.AddDays(3) }.Save(_statusPath);

        CreateService().IsCheckDue(DateTime.UtcNow).Should().BeFalse(
            "a service restart within the weekly window must not re-check");
    }

    [Fact]
    public void IsCheckDue_NextCheckPassed_IsDue()
    {
        new UpdateCheckStatus { NextCheckUtc = DateTime.UtcNow.AddMinutes(-1) }.Save(_statusPath);

        CreateService().IsCheckDue(DateTime.UtcNow).Should().BeTrue();
    }

    // ── RunCheckAsync: status file content ────────────────────────────────

    [Fact]
    public async Task RunCheck_Success_WritesStatus_WithWeeklyNextCheck()
    {
        _checker.CheckAsync(Arg.Any<CancellationToken>()).Returns(SuccessResult(updateAvailable: false, latest: "1.2.0.196"));

        await CreateService().RunCheckAsync(CancellationToken.None);

        var status = UpdateCheckStatus.TryLoad(_statusPath)!;
        status.Should().NotBeNull();
        status.CurrentVersion.Should().Be("1.2.0.196");
        status.LatestVersion.Should().Be("1.2.0.196");
        status.UpdateAvailable.Should().BeFalse();
        status.LastError.Should().BeNull();
        status.LastCheckUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        status.NextCheckUtc.Should().BeCloseTo(DateTime.UtcNow + UpdateCheckService.CheckInterval, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task RunCheck_UpToDate_DoesNotNotify()
    {
        _checker.CheckAsync(Arg.Any<CancellationToken>()).Returns(SuccessResult(updateAvailable: false, latest: "1.2.0.196"));

        await CreateService().RunCheckAsync(CancellationToken.None);

        await _notifications.DidNotReceive().NotifyUpdateAvailableAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ── Notification dedupe: one mail per new version ─────────────────────

    [Fact]
    public async Task RunCheck_UpdateAvailable_NotifiesOnce_AndPersistsNotifiedVersion()
    {
        _checker.CheckAsync(Arg.Any<CancellationToken>()).Returns(SuccessResult(updateAvailable: true));
        var svc = CreateService();

        await svc.RunCheckAsync(CancellationToken.None);
        await svc.RunCheckAsync(CancellationToken.None);   // next weekly check, same release

        await _notifications.Received(1).NotifyUpdateAvailableAsync(
            "1.2.0.196", "1.3.0.210", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        UpdateCheckStatus.TryLoad(_statusPath)!.LastNotifiedVersion.Should().Be("1.3.0.210");
    }

    [Fact]
    public async Task RunCheck_EvenNewerRelease_NotifiesAgain()
    {
        _checker.CheckAsync(Arg.Any<CancellationToken>()).Returns(SuccessResult(updateAvailable: true, latest: "1.3.0.210"));
        var svc = CreateService();
        await svc.RunCheckAsync(CancellationToken.None);

        _checker.CheckAsync(Arg.Any<CancellationToken>()).Returns(SuccessResult(updateAvailable: true, latest: "1.4.0.230"));
        await svc.RunCheckAsync(CancellationToken.None);

        await _notifications.Received(1).NotifyUpdateAvailableAsync(
            "1.2.0.196", "1.3.0.210", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _notifications.Received(1).NotifyUpdateAvailableAsync(
            "1.2.0.196", "1.4.0.230", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ── "Check now" request file (dropped by the ConfigTool) ──────────────

    [Fact]
    public void ConsumeCheckRequest_FilePresent_ReturnsTrue_AndDeletesIt()
    {
        File.WriteAllText(_requestPath, DateTime.UtcNow.ToString("O"));
        var svc = CreateService();

        svc.ConsumeCheckRequest().Should().BeTrue();
        File.Exists(_requestPath).Should().BeFalse("the request is one-shot");
        svc.ConsumeCheckRequest().Should().BeFalse();
    }

    // ── Failed checks ─────────────────────────────────────────────────────

    [Fact]
    public async Task RunCheck_Failure_KeepsPreviousResult_SetsError_RetriesTomorrow_NoMail()
    {
        _checker.CheckAsync(Arg.Any<CancellationToken>()).Returns(SuccessResult(updateAvailable: true));
        var svc = CreateService();
        await svc.RunCheckAsync(CancellationToken.None);
        _notifications.ClearReceivedCalls();

        _checker.CheckAsync(Arg.Any<CancellationToken>())
            .Returns(new UpdateCheckResult(false, "1.2.0.196", Error: "GitHub API returned 503 Service Unavailable"));
        await svc.RunCheckAsync(CancellationToken.None);

        var status = UpdateCheckStatus.TryLoad(_statusPath)!;
        status.LastError.Should().Contain("503");
        status.LatestVersion.Should().Be("1.3.0.210", "the last successful result stays visible in the ConfigTool");
        status.UpdateAvailable.Should().BeTrue();
        status.LastNotifiedVersion.Should().Be("1.3.0.210", "a failed check must not reset the dedupe marker");
        status.NextCheckUtc.Should().BeCloseTo(DateTime.UtcNow + UpdateCheckService.RetryInterval, TimeSpan.FromMinutes(1),
            "a failed check retries after a day instead of a week");
        await _notifications.DidNotReceive().NotifyUpdateAvailableAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
