using GraphMailer.ConfigTool.Helpers;

namespace GraphMailer.Tests.Unit.ConfigTool;

public sealed class DecryptionFailureMapTests
{
    [Theory]
    [InlineData("Users[0].Password", 0)]
    [InlineData("Users[12].Password", 12)]
    public void UserPasswordIndex_UserPath_ReturnsIndex(string path, int expected)
    {
        DecryptionFailureMap.UserPasswordIndex(path).Should().Be(expected);
    }

    [Theory]
    [InlineData("GraphApi.ClientSecret")]
    [InlineData("Users[].Password")]
    [InlineData("Users[0].Username")]
    [InlineData("")]
    public void UserPasswordIndex_NonUserPasswordPath_ReturnsNull(string path)
    {
        DecryptionFailureMap.UserPasswordIndex(path).Should().BeNull();
    }

    [Fact]
    public void HasGraphApiFailure_WhenClientSecretPresent_ReturnsTrue()
    {
        DecryptionFailureMap.HasGraphApiFailure(["GraphApi.ClientSecret"]).Should().BeTrue();
    }

    [Fact]
    public void HasGraphApiFailure_WhenOnlyUserPasswords_ReturnsFalse()
    {
        DecryptionFailureMap.HasGraphApiFailure(["Users[0].Password"]).Should().BeFalse();
    }

    [Fact]
    public void HasUserFailure_WhenUserPasswordPresent_ReturnsTrue()
    {
        DecryptionFailureMap.HasUserFailure(["Users[3].Password"]).Should().BeTrue();
    }

    [Fact]
    public void HasUserFailure_WhenOnlyGraphSecret_ReturnsFalse()
    {
        DecryptionFailureMap.HasUserFailure(["GraphApi.ClientSecret"]).Should().BeFalse();
    }
}
