using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GraphMailer.Tests.Unit.Infrastructure.Validation;

public sealed class SmtpOptionsValidatorTests
{
    // =========================================================================
    // Valid configurations
    // =========================================================================

    [Fact]
    public void Validate_DefaultMaxSizeBytes_Succeeds()
    {
        var sut = BuildSut();
        var result = sut.Validate(null, new SmtpOptions());

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(1)]                     // minimum
    [InlineData(1_024)]                 // 1 KB
    [InlineData(26_214_400)]            // default 25 MB
    [InlineData(36_700_160)]            // EXO default org receive limit ~35 MB
    [InlineData(157_286_400)]           // 150 MB – exactly at the Exchange Online hard limit
    public void Validate_ValidMaxSizeBytes_Succeeds(long maxSizeBytes)
    {
        var sut = BuildSut();
        var opts = new SmtpOptions { MaxSizeBytes = maxSizeBytes };

        sut.Validate(null, opts).Succeeded.Should().BeTrue(
            $"MaxSizeBytes = {maxSizeBytes} should be accepted");
    }

    // =========================================================================
    // Invalid configurations → Fail
    // =========================================================================

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void Validate_ZeroOrNegativeMaxSizeBytes_Fails(long maxSizeBytes)
    {
        var sut = BuildSut();
        var opts = new SmtpOptions { MaxSizeBytes = maxSizeBytes };

        var result = sut.Validate(null, opts);

        result.Failed.Should().BeTrue(
            $"MaxSizeBytes = {maxSizeBytes} must be rejected (must be positive)");
        result.FailureMessage.Should().Contain("MaxSizeBytes");
    }

    // =========================================================================
    // Warning-only: above Exchange Online hard limit
    // =========================================================================

    [Theory]
    [InlineData(157_286_401L)]       // 150 MB + 1 byte
    [InlineData(200L * 1024 * 1024)] // 200 MB
    public void Validate_AboveExchangeOnlineLimit_SucceedsWithWarning(long maxSizeBytes)
    {
        // The validator must NOT fail at startup – it only issues a warning.
        // Failing here would prevent the service from starting at all, which is worse
        // than accepting the misconfigured value and logging a warning.
        var sut = BuildSut();
        var opts = new SmtpOptions { MaxSizeBytes = maxSizeBytes };

        var result = sut.Validate(null, opts);

        result.Succeeded.Should().BeTrue(
            $"MaxSizeBytes = {maxSizeBytes} exceeds EXO limit but should only warn, not fail startup");
    }

    // =========================================================================
    // ExchangeOnlineMaxBytes constant
    // =========================================================================

    [Fact]
    public void ExchangeOnlineMaxBytes_Is150Mb()
    {
        SmtpOptionsValidator.ExchangeOnlineMaxBytes.Should().Be(150L * 1024 * 1024);
    }

    // =========================================================================
    // Helper
    // =========================================================================

    private static SmtpOptionsValidator BuildSut() =>
        new(NullLogger<SmtpOptionsValidator>.Instance);
}
