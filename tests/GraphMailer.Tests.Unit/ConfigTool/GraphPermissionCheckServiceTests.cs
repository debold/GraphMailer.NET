using FluentAssertions;
using GraphMailer.ConfigTool.Services;
using GraphMailer.Service.Configuration;

namespace GraphMailer.Tests.Unit.ConfigTool;

/// <summary>
/// Tests for the ConfigTool's Graph permission check.
///
/// Only the paths that need no tenant are covered here — acquiring a real app-only token belongs
/// to the live tests. What matters at this level is that an unanswerable question reports
/// <see cref="GraphPermissionState.Unknown"/> rather than a gap: the whole point of the check is
/// to stop the ConfigTool from making claims it cannot back up, and "no credentials configured"
/// is not evidence that permissions are missing.
/// </summary>
public sealed class GraphPermissionCheckServiceTests
{
    private static readonly SenderValidationOptions ValidationOff = new() { Enabled = false };

    [Fact]
    public async Task Check_NoTenantId_IsUnknownWithReason()
    {
        var result = await GraphPermissionCheckService.CheckAsync(
            null, "client-id", "s3cr3t", null, ValidationOff, CancellationToken.None);

        result.State.Should().Be(GraphPermissionState.Unknown);
        result.Missing.Should().BeEmpty();
        result.Reason.Should().Contain("Tenant ID");
    }

    [Fact]
    public async Task Check_NoClientId_IsUnknownWithReason()
    {
        var result = await GraphPermissionCheckService.CheckAsync(
            "tenant-id", "  ", "s3cr3t", null, ValidationOff, CancellationToken.None);

        result.State.Should().Be(GraphPermissionState.Unknown);
        result.Reason.Should().Contain("Client ID");
    }

    [Fact]
    public async Task Check_NoSecretAndNoCertificate_IsUnknownWithReason()
    {
        var result = await GraphPermissionCheckService.CheckAsync(
            "tenant-id", "client-id", null, null, ValidationOff, CancellationToken.None);

        result.State.Should().Be(GraphPermissionState.Unknown);
        result.Reason.Should().Contain("certificate");
    }

    /// <summary>A thumbprint that matches no installed certificate throws inside MSAL. The check
    /// is a diagnostic on a page that must keep working, so it reports the failure instead of
    /// propagating it.</summary>
    [Fact]
    public async Task Check_CertificateNotInstalled_IsUnknownAndDoesNotThrow()
    {
        var result = await GraphPermissionCheckService.CheckAsync(
            "tenant-id", "client-id", null, "0000000000000000000000000000000000000000",
            ValidationOff, CancellationToken.None);

        result.State.Should().Be(GraphPermissionState.Unknown);
        result.Reason.Should().NotBeNullOrWhiteSpace();
        result.Missing.Should().BeEmpty();
    }
}
