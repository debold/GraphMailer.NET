using FluentAssertions;
using GraphMailer.Service.Services.Reporting;

namespace GraphMailer.Tests.Unit.Services.Reporting;

public sealed class NotificationSubjectTests
{
    [Fact]
    public void Build_WithPrefix_PutsPrefixInFrontOfSubject()
        => NotificationSubject.Build("[GraphMailer]", "Connection test")
            .Should().Be("[GraphMailer] Connection test");

    [Fact]
    public void Build_CustomPrefix_IsUsedVerbatim()
        => NotificationSubject.Build("[MAIL-PROD]", "Certificate expiring soon")
            .Should().Be("[MAIL-PROD] Certificate expiring soon");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_MissingPrefix_ReturnsBareSubjectWithoutLeadingSpace(string? prefix)
        => NotificationSubject.Build(prefix, "Connection test").Should().Be("Connection test");

    [Fact]
    public void Build_PrefixWithSurroundingWhitespace_IsTrimmedToASingleSeparator()
        => NotificationSubject.Build("  [GM]  ", "Queue stalled").Should().Be("[GM] Queue stalled");
}
