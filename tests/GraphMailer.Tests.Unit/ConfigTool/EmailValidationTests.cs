using GraphMailer.ConfigTool.Helpers;

namespace GraphMailer.Tests.Unit.ConfigTool;

public sealed class EmailValidationTests
{
    [Theory]
    [InlineData("jane.doe@contoso.com")]
    [InlineData("ops@corp.com")]
    [InlineData("a.b.c@sub.domain.co.uk")]
    [InlineData("  spaced@corp.com  ")]
    public void IsValidRecipient_ValidAddresses_ReturnTrue(string address)
    {
        EmailValidation.IsValidRecipient(address).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not-an-email")]
    [InlineData("missing@domain")]
    [InlineData("Jane Doe <jane@contoso.com>")]   // display-name form rejected
    public void IsValidRecipient_InvalidAddresses_ReturnFalse(string? address)
    {
        EmailValidation.IsValidRecipient(address).Should().BeFalse();
    }
}
