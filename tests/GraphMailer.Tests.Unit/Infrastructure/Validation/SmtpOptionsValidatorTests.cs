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
    // MaxRecipients — must stay inside Microsoft's per-mailbox range
    // =========================================================================

    [Theory]
    [InlineData(1)]      // Microsoft's minimum
    [InlineData(500)]    // Exchange Online default
    [InlineData(1000)]   // Microsoft's maximum
    public void Validate_ValidMaxRecipients_Succeeds(int maxRecipients)
    {
        var sut = BuildSut();
        var opts = new SmtpOptions { MaxRecipients = maxRecipients };

        sut.Validate(null, opts).Succeeded.Should().BeTrue(
            $"MaxRecipients = {maxRecipients} is within the range Exchange Online allows");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    [InlineData(int.MaxValue)]
    public void Validate_MaxRecipientsOutsideMicrosoftRange_Fails(int maxRecipients)
    {
        var sut = BuildSut();
        var opts = new SmtpOptions { MaxRecipients = maxRecipients };

        var result = sut.Validate(null, opts);

        result.Failed.Should().BeTrue(
            $"MaxRecipients = {maxRecipients} is outside the 1-1000 range Exchange Online allows");
        result.FailureMessage.Should().Contain("MaxRecipients");
    }

    [Fact]
    public void Validate_DefaultMaxRecipients_Is500()
    {
        // The default must match Exchange Online's own default, so an operator who never
        // touches the setting behaves exactly as before it was configurable.
        new SmtpOptions().MaxRecipients.Should().Be(500);
    }

    // =========================================================================
    // Range constants
    // =========================================================================

    [Fact]
    public void ExchangeOnlineMaxBytes_Is150Mb()
    {
        SmtpOptionsValidator.ExchangeOnlineMaxBytes.Should().Be(150L * 1024 * 1024);
    }

    [Fact]
    public void RecipientRange_MatchesMicrosoftLimits()
    {
        SmtpOptionsValidator.MinRecipients.Should().Be(1);
        SmtpOptionsValidator.MaxRecipients.Should().Be(1000);
    }

    // =========================================================================
    // Helper
    // =========================================================================

    private static SmtpOptionsValidator BuildSut() =>
        new(NullLogger<SmtpOptionsValidator>.Instance);
}
