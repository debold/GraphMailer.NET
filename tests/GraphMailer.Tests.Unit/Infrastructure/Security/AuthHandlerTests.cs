using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GraphMailer.Tests.Unit.Infrastructure.Security;

public sealed class AuthHandlerTests : IDisposable
{
    private readonly string _keyDir = Path.Combine(Path.GetTempPath(), "authhandler-tests-" + Guid.NewGuid().ToString("N"));
    private readonly IDataProtectionProvider _dp;
    private readonly ServiceProvider _sp;

    public AuthHandlerTests()
    {
        Directory.CreateDirectory(_keyDir);
        var services = new ServiceCollection();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(_keyDir));
        _sp = services.BuildServiceProvider();
        _dp = _sp.GetRequiredService<IDataProtectionProvider>();
    }

    public void Dispose()
    {
        _sp.Dispose();
        if (Directory.Exists(_keyDir))
            Directory.Delete(_keyDir, recursive: true);
    }

    // -------------------------------------------------------------------------
    // Plaintext password
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidateUser_PlaintextPassword_ValidCredentials_ReturnsTrue()
    {
        var sut = BuildSut([new UserEntry { Username = "alice", Password = "secret" }]);
        sut.ValidateUser("alice", "secret", out _).Should().BeTrue();
    }

    [Fact]
    public void ValidateUser_PlaintextPassword_WrongPassword_ReturnsFalse()
    {
        var sut = BuildSut([new UserEntry { Username = "alice", Password = "secret" }]);
        sut.ValidateUser("alice", "wrong", out _).Should().BeFalse();
    }

    [Fact]
    public void ValidateUser_UnknownUser_ReturnsFalse()
    {
        var sut = BuildSut([new UserEntry { Username = "alice", Password = "secret" }]);
        sut.ValidateUser("bob", "secret", out _).Should().BeFalse();
    }

    [Fact]
    public void ValidateUser_UsernameIsCaseInsensitive()
    {
        var sut = BuildSut([new UserEntry { Username = "Alice", Password = "pw" }]);
        sut.ValidateUser("ALICE", "pw", out _).Should().BeTrue();
    }

    [Fact]
    public void ValidateUser_NoUsers_ReturnsFalse()
    {
        var sut = BuildSut([]);
        sut.ValidateUser("anyone", "pw", out _).Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // ENC[...] encrypted password
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidateUser_EncryptedPassword_ValidCredentials_ReturnsTrue()
    {
        var protector = _dp.CreateProtector("GraphMailer.Configuration.v1");
        var encrypted = "ENC[" + protector.Protect("s3cr3t") + "]";

        var sut = BuildSut([new UserEntry { Username = "bob", Password = encrypted }]);
        sut.ValidateUser("bob", "s3cr3t", out _).Should().BeTrue();
    }

    [Fact]
    public void ValidateUser_EncryptedPassword_WrongPassword_ReturnsFalse()
    {
        var protector = _dp.CreateProtector("GraphMailer.Configuration.v1");
        var encrypted = "ENC[" + protector.Protect("s3cr3t") + "]";

        var sut = BuildSut([new UserEntry { Username = "bob", Password = encrypted }]);
        sut.ValidateUser("bob", "wrong", out _).Should().BeFalse();
    }

    [Fact]
    public void ValidateUser_CorruptEncryptedPassword_ReturnsFalse()
    {
        var sut = BuildSut([new UserEntry { Username = "eve", Password = "ENC[not-valid-ciphertext]" }]);
        sut.ValidateUser("eve", "anything", out _).Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // GetFromRestrictions
    // -------------------------------------------------------------------------

    [Fact]
    public void GetFromRestrictions_UserWithRestrictions_ReturnsThemList()
    {
        var sut = BuildSut([new UserEntry
        {
            Username = "carol",
            Password = "pw",
            FromRestrictions = ["carol@example.com", "@example.com"]
        }]);

        var restrictions = sut.GetFromRestrictions("carol");
        restrictions.Should().NotBeNull();
        restrictions.Should().Contain("carol@example.com");
    }

    [Fact]
    public void GetFromRestrictions_UserWithoutRestrictions_ReturnsNull()
    {
        var sut = BuildSut([new UserEntry { Username = "dave", Password = "pw" }]);
        sut.GetFromRestrictions("dave").Should().BeNull();
    }

    [Fact]
    public void GetFromRestrictions_UnknownUser_ReturnsNull()
    {
        var sut = BuildSut([]);
        sut.GetFromRestrictions("nobody").Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // -------------------------------------------------------------------------
    // Edge cases
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidateUser_NullPassword_ReturnsFalse()
    {
        // UserEntry.Password is nullable; null means no password is configured.
        var sut = BuildSut([new UserEntry { Username = "ghost", Password = null }]);
        sut.ValidateUser("ghost", "anything", out _).Should().BeFalse();
    }

    [Fact]
    public void ValidateUser_EmptyPassword_ReturnsFalse()
    {
        var sut = BuildSut([new UserEntry { Username = "empty", Password = "" }]);
        sut.ValidateUser("empty", "", out _).Should().BeFalse();
    }

    [Fact]
    public void ValidateUser_MultipleUsers_CorrectUserSelected()
    {
        var sut = BuildSut([
            new UserEntry { Username = "alice", Password = "pw-alice" },
            new UserEntry { Username = "bob",   Password = "pw-bob"   },
            new UserEntry { Username = "carol", Password = "pw-carol" },
        ]);

        sut.ValidateUser("bob", "pw-bob", out _).Should().BeTrue();
        sut.ValidateUser("bob", "pw-alice", out _).Should().BeFalse();
        sut.ValidateUser("alice", "pw-alice", out _).Should().BeTrue();
    }

    [Fact]
    public void ValidateUser_EmptyUsername_ReturnsFalse()
    {
        var sut = BuildSut([new UserEntry { Username = "alice", Password = "pw" }]);
        sut.ValidateUser("", "pw", out _).Should().BeFalse();
    }

    [Fact]
    public void GetFromRestrictions_CaseInsensitiveUsername()
    {
        var sut = BuildSut([new UserEntry
        {
            Username = "Dave",
            Password = "pw",
            FromRestrictions = ["dave@example.com"]
        }]);

        sut.GetFromRestrictions("DAVE").Should().NotBeNull();
        sut.GetFromRestrictions("dave").Should().NotBeNull();
    }

    // -------------------------------------------------------------------------
    // Log output tests
    // =========================================================================
    // When to use FakeLogger: only for security-critical log entries where the
    // log IS the operator's notification channel – e.g. a corrupt password in
    // config that the operator must fix. Do NOT test debug/info log lines.
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidateUser_CorruptEncryptedPassword_LogsError()
    {
        var logger = new FakeLogger<AuthHandler>();
        var access = new SmtpAccessOptions
        {
            Users = [new UserEntry { Username = "eve", Password = "ENC[not-valid-ciphertext]" }]
        };
        var sut = new AuthHandler(new TestOptionsMonitor<SmtpAccessOptions>(access), _dp, logger);

        sut.ValidateUser("eve", "anything", out _); // triggers the decrypt error

        logger.HasEntry(LogLevel.Error, "Failed to decrypt").Should().BeTrue(
            "a corrupt ENC[...] password must be logged as an error so the operator knows to fix the config");
    }

    [Fact]
    public void ValidateUser_CaptureMode_LogsWarningAboutOpenWindow()
    {
        // While CaptureNextPassword is set, ANY password authenticates the user —
        // the warning log is the operator's only signal that this window is open.
        var logger = new FakeLogger<AuthHandler>();
        var access = new SmtpAccessOptions
        {
            Users = [new UserEntry { Username = "carol", CaptureNextPassword = true }]
        };
        var sut = new AuthHandler(new TestOptionsMonitor<SmtpAccessOptions>(access), _dp, logger);

        var valid = sut.ValidateUser("carol", "literally-anything", out var captureRequired);

        valid.Should().BeTrue();
        captureRequired.Should().BeTrue();
        logger.HasEntry(LogLevel.Warning, "ANY password").Should().BeTrue(
            "each capture-window authentication must be visible at Warning level");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    // -------------------------------------------------------------------------
    // Enabled flag
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidateUser_DisabledUser_ReturnsFalse_EvenWithCorrectPassword()
    {
        var sut = BuildSut([new UserEntry { Username = "alice", Password = "secret", Enabled = false }]);
        sut.ValidateUser("alice", "secret", out _).Should().BeFalse(
            "a user disabled in the ConfigTool must not be able to authenticate");
    }

    [Fact]
    public void ValidateUser_DisabledUser_CaptureModeDoesNotBypass()
    {
        var sut = BuildSut([new UserEntry { Username = "alice", CaptureNextPassword = true, Enabled = false }]);
        sut.ValidateUser("alice", "anything", out var captureRequired).Should().BeFalse();
        captureRequired.Should().BeFalse("capture mode must not activate for disabled users");
    }

    [Fact]
    public void ValidateUser_EnabledDefaultsToTrue_ForConfigsWithoutTheFlag()
    {
        // UserEntry created without Enabled (older configs) must keep working
        var sut = BuildSut([new UserEntry { Username = "alice", Password = "secret" }]);
        sut.ValidateUser("alice", "secret", out _).Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Failure reasons (log diagnostics; never sent to the SMTP client)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("bob", "secret", "unknown user")]
    [InlineData("alice", "wrong", "wrong password")]
    public void ValidateUser_Failure_ReportsReason(string login, string password, string expectedReason)
    {
        var sut = BuildSut([new UserEntry { Username = "alice", Password = "secret" }]);

        sut.ValidateUser(login, password, out _, out var reason).Should().BeFalse();
        reason.Should().Be(expectedReason);
    }

    [Fact]
    public void ValidateUser_DisabledUser_ReportsReason()
    {
        var sut = BuildSut([new UserEntry { Username = "alice", Password = "secret", Enabled = false }]);

        sut.ValidateUser("alice", "secret", out _, out var reason).Should().BeFalse();
        reason.Should().Be("user is disabled");
    }

    [Fact]
    public void ValidateUser_NoPasswordConfigured_ReportsReason()
    {
        var sut = BuildSut([new UserEntry { Username = "alice", Password = null }]);

        sut.ValidateUser("alice", "anything", out _, out var reason).Should().BeFalse();
        reason.Should().Be("no usable password configured");
    }

    [Fact]
    public void ValidateUser_Success_ReasonIsNull()
    {
        var sut = BuildSut([new UserEntry { Username = "alice", Password = "secret" }]);

        sut.ValidateUser("alice", "secret", out _, out var reason).Should().BeTrue();
        reason.Should().BeNull();
    }

    private AuthHandler BuildSut(List<UserEntry> users)
    {
        var access = new SmtpAccessOptions { Users = users };
        return new AuthHandler(
            new TestOptionsMonitor<SmtpAccessOptions>(access),
            _dp,
            NullLogger<AuthHandler>.Instance);
    }
}
