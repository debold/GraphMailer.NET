using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Config;
using GraphMailer.Service.Infrastructure.Encryption;
using GraphMailer.Service.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using OptionsUser = GraphMailer.Service.Configuration.UserEntry;

namespace GraphMailer.Tests.Unit.Infrastructure.Security;

/// <summary>
/// Password capture: when an admin arms CaptureNextPassword for an SMTP user, the next
/// successful AUTH stores that password. Every branch decides whether a password is written to
/// disk, so each is covered: armed or not, unknown user, a flag cleared by a concurrent capture,
/// and a config file that cannot be read. The stored value must be encrypted, never plaintext.
/// </summary>
public sealed class PasswordCaptureServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gm-pwcapture-" + Guid.NewGuid().ToString("N"));
    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();
    private readonly FakeLogger<PasswordCaptureService> _logger = new();

    public PasswordCaptureServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string ConfigPath => Path.Combine(_dir, "graphmailer.json");

    private ConfigService Config()
        => new(ConfigPath, _dataProtection.CreateProtector(DataProtectionExtensions.ConfigPurpose));

    /// <summary>Writes graphmailer.json with the given users (passwords encrypted by Save).</summary>
    private void SeedConfig(params ConfigDocument.UserEntry[] users)
    {
        var doc = new ConfigDocument();
        doc.Access.Users.AddRange(users);
        Config().Save(doc);
    }

    private ConfigDocument.UserEntry StoredUser(string username)
        => Config().Load().Access.Users.Single(u => u.Username == username);

    private PasswordCaptureService Sut(params OptionsUser[] runtimeUsers)
    {
        var access = Substitute.For<IOptionsMonitor<SmtpAccessOptions>>();
        access.CurrentValue.Returns(new SmtpAccessOptions { Users = [.. runtimeUsers] });
        return new PasswordCaptureService(access, _dataProtection, _logger) { ConfigPath = ConfigPath };
    }

    // =========================================================================
    // IsCaptureEnabled — read from the live options snapshot
    // =========================================================================

    [Fact]
    public void IsCaptureEnabled_FlagSet_ReturnsTrueIgnoringUsernameCase()
    {
        var sut = Sut(new OptionsUser { Username = "Relay", CaptureNextPassword = true });

        sut.IsCaptureEnabled("relay").Should().BeTrue();
    }

    [Fact]
    public void IsCaptureEnabled_FlagNotSet_ReturnsFalse()
    {
        var sut = Sut(new OptionsUser { Username = "relay", CaptureNextPassword = false });

        sut.IsCaptureEnabled("relay").Should().BeFalse();
    }

    [Fact]
    public void IsCaptureEnabled_UnknownUser_ReturnsFalse()
    {
        var sut = Sut(new OptionsUser { Username = "relay", CaptureNextPassword = true });

        sut.IsCaptureEnabled("someone-else").Should().BeFalse();
    }

    // =========================================================================
    // CaptureAsync — what gets written to graphmailer.json
    // =========================================================================

    [Fact]
    public async Task CaptureAsync_Armed_StoresThePasswordAndClearsTheFlag()
    {
        SeedConfig(new ConfigDocument.UserEntry { Username = "relay", Password = "old", CaptureNextPassword = true });

        await Sut().CaptureAsync("relay", "n3w-pa55");

        var user = StoredUser("relay");
        user.Password.Should().Be("n3w-pa55");
        user.CaptureNextPassword.Should().BeFalse("the flag is one-shot — the next login must not overwrite it");
    }

    [Fact]
    public async Task CaptureAsync_Armed_WritesThePasswordEncrypted()
    {
        SeedConfig(new ConfigDocument.UserEntry { Username = "relay", CaptureNextPassword = true });

        await Sut().CaptureAsync("relay", "n3w-pa55");

        var raw = await File.ReadAllTextAsync(ConfigPath);
        raw.Should().NotContain("n3w-pa55");
        raw.Should().Contain("ENC[");
    }

    [Fact]
    public async Task CaptureAsync_UsernameInDifferentCase_StillCaptures()
    {
        SeedConfig(new ConfigDocument.UserEntry { Username = "Relay", CaptureNextPassword = true });

        await Sut().CaptureAsync("relay", "n3w-pa55");

        StoredUser("Relay").Password.Should().Be("n3w-pa55");
    }

    [Fact]
    public async Task CaptureAsync_FlagAlreadyCleared_LeavesThePasswordUnchanged()
    {
        // A concurrent capture won the race and cleared the flag, or the admin disarmed it.
        SeedConfig(new ConfigDocument.UserEntry { Username = "relay", Password = "kept", CaptureNextPassword = false });

        await Sut().CaptureAsync("relay", "should-not-be-stored");

        StoredUser("relay").Password.Should().Be("kept");
    }

    [Fact]
    public async Task CaptureAsync_UnknownUser_ChangesNothingAndWarns()
    {
        SeedConfig(new ConfigDocument.UserEntry { Username = "relay", Password = "kept", CaptureNextPassword = true });
        var before = await File.ReadAllTextAsync(ConfigPath);

        await Sut().CaptureAsync("ghost", "pw");

        (await File.ReadAllTextAsync(ConfigPath)).Should().Be(before);
        _logger.HasEntry(LogLevel.Warning, "not found").Should().BeTrue();
    }

    [Fact]
    public async Task CaptureAsync_OnlyTheCapturedUserChanges()
    {
        SeedConfig(
            new ConfigDocument.UserEntry { Username = "relay", CaptureNextPassword = true },
            new ConfigDocument.UserEntry { Username = "other", Password = "untouched", CaptureNextPassword = true });

        await Sut().CaptureAsync("relay", "n3w-pa55");

        var other = StoredUser("other");
        other.Password.Should().Be("untouched");
        other.CaptureNextPassword.Should().BeTrue("the other user's own capture is still pending");
    }

    [Fact]
    public async Task CaptureAsync_UnreadableConfig_DoesNotThrowAndLogsAnError()
    {
        // Fire-and-forget from the SMTP pipeline: the AUTH reply is already sent, so a failed
        // write must surface in the log and nowhere else.
        await File.WriteAllTextAsync(ConfigPath, "{ this is not json");

        var act = () => Sut().CaptureAsync("relay", "pw");

        await act.Should().NotThrowAsync();
        _logger.HasEntry(LogLevel.Error, "Failed to persist").Should().BeTrue();
    }
}
