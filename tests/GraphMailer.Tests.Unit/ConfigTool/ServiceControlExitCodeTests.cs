using GraphMailer.ConfigTool.Helpers;

namespace GraphMailer.Tests.Unit.ConfigTool;

/// <summary>
/// Which sc.exe exit codes count as "the command did what we wanted".
///
/// The Restart button used to discard these entirely and always report success, so a service that
/// stopped and never came back looked fine in the UI. The tolerated codes are the subtle part: two
/// of them are not failures at all, and treating them as such would make Restart report a problem
/// where there is none.
/// </summary>
public sealed class ServiceControlExitCodeTests
{
    [Fact]
    public void IsStopAccepted_Success()
        => ServiceControl.IsStopAccepted(0).Should().BeTrue();

    [Fact]
    public void IsStopAccepted_AlreadyStopped_IsFine()
        => ServiceControl.IsStopAccepted(ServiceControl.ErrorServiceNotActive).Should().BeTrue(
            "restarting a service that was already down is not an error");

    [Theory]
    [InlineData(5)]      // access denied
    [InlineData(1060)]   // service does not exist
    [InlineData(1072)]   // marked for deletion
    public void IsStopAccepted_RealFailures_AreRejected(int exitCode)
        => ServiceControl.IsStopAccepted(exitCode).Should().BeFalse();

    [Fact]
    public void IsStartAccepted_Success()
        => ServiceControl.IsStartAccepted(0).Should().BeTrue();

    [Fact]
    public void IsStartAccepted_RequestTimeout_IsFine()
        => ServiceControl.IsStartAccepted(ServiceControl.ErrorServiceRequestTimeout).Should().BeTrue(
            "a slow start is settled by polling the state, not by the exit code");

    [Fact]
    public void IsStartAccepted_AlreadyRunning_IsFine()
        => ServiceControl.IsStartAccepted(ServiceControl.ErrorServiceAlreadyRunning).Should().BeTrue(
            "the SCM can still hold the previous process right after a stop");

    [Theory]
    [InlineData(5)]      // access denied
    [InlineData(1060)]   // service does not exist
    [InlineData(1069)]   // logon failure
    public void IsStartAccepted_RealFailures_AreRejected(int exitCode)
        => ServiceControl.IsStartAccepted(exitCode).Should().BeFalse();

    [Fact]
    public void StopAndStart_DoNotShareTheirTolerances()
    {
        // "Already running" must never excuse a failed stop, and "not active" must never excuse a
        // failed start — each would hide the opposite half of a restart going wrong.
        ServiceControl.IsStopAccepted(ServiceControl.ErrorServiceAlreadyRunning).Should().BeFalse();
        ServiceControl.IsStartAccepted(ServiceControl.ErrorServiceNotActive).Should().BeFalse();
    }
}
