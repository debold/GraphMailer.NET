using GraphMailer.Service.Infrastructure.Encryption;
using Microsoft.AspNetCore.DataProtection;

namespace GraphMailer.Tests.Unit.Infrastructure.Encryption;

/// <summary>
/// Lifetime contract of the standalone config protector.
///
/// Regression: <see cref="DataProtectionExtensions.BuildConfigProtector"/> used to dispose
/// the backing <see cref="System.IServiceProvider"/> (a `using`) before returning the
/// protector. The protector could still Unprotect (so config LOAD worked) but Protect
/// threw "An error occurred while trying to encrypt the provided data"
/// (inner: ObjectDisposedException) — which is why the ConfigTool could open a
/// configuration but not save it. The provider must stay alive for the process lifetime.
/// Windows-only, like the product itself.
/// </summary>
public sealed class DataProtectionExtensionsTests
{
    [Fact]
    public void BuildConfigProtector_CanProtectAfterReturn_NotOnlyUnprotect()
    {
        var protector = DataProtectionExtensions.BuildConfigProtector();

        // The Protect call is the regression: it failed when the provider was disposed.
        var cipher = protector.Protect("backup-password");
        protector.Unprotect(cipher).Should().Be("backup-password");
    }

    [Fact]
    public void BuildConfigProtector_TwoInstances_ShareTheSameKeyRing()
    {
        // The ConfigTool and the service each build their own protector from the same
        // key ring; a value encrypted by one must decrypt with the other.
        var a = DataProtectionExtensions.BuildConfigProtector();
        var b = DataProtectionExtensions.BuildConfigProtector();

        b.Unprotect(a.Protect("shared-secret")).Should().Be("shared-secret");
    }
}
