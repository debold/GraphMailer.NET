using System.Security.Cryptography;
using GraphMailer.Service.Infrastructure.Encryption;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace GraphMailer.Tests.Unit.Infrastructure.Encryption;

/// <summary>
/// The service reads graphmailer.json through this provider, which turns every ENC[...] value
/// back into the secret. The failure branches matter most: a value that cannot be decrypted
/// must never reach the service as the raw ciphertext (it would be used as a password or client
/// secret), and the required file must fail loudly instead.
/// </summary>
public sealed class EncryptedJsonConfigurationProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gm-encjson-" + Guid.NewGuid().ToString("N"));
    private readonly IDataProtector _protector =
        new EphemeralDataProtectionProvider().CreateProtector(DataProtectionExtensions.ConfigPurpose);

    public EncryptedJsonConfigurationProviderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_dir, "graphmailer.json");
        File.WriteAllText(path, json);
        return path;
    }

    private IConfigurationRoot Load(string path, IDataProtector protector, bool optional = true)
        => new ConfigurationBuilder()
            .AddEncryptedJsonFile(path, protector, optional, reloadOnChange: false)
            .Build();

    private string Enc(string plaintext) => $"ENC[{_protector.Protect(plaintext)}]";

    // =========================================================================
    // Decryption
    // =========================================================================

    [Fact]
    public void Load_EncValue_IsDecrypted()
    {
        var path = WriteConfig($$"""{ "GraphApi": { "ClientSecret": "{{Enc("s3cr3t")}}" } }""");

        Load(path, _protector)["GraphApi:ClientSecret"].Should().Be("s3cr3t");
    }

    [Fact]
    public void Load_EncValueInsideAnArray_IsDecrypted()
    {
        // SMTP user passwords live in an array (Access:Users:n:Password).
        var path = WriteConfig($$"""
            { "Access": { "Users": [
                { "Username": "a", "Password": "{{Enc("first")}}" },
                { "Username": "b", "Password": "{{Enc("second")}}" } ] } }
            """);

        var config = Load(path, _protector);

        config["Access:Users:0:Password"].Should().Be("first");
        config["Access:Users:1:Password"].Should().Be("second");
    }

    [Theory]
    [InlineData("plain-value")]
    [InlineData("ENC[missing-suffix")]
    [InlineData("prefix ENC[not-at-start]")]
    [InlineData("enc[lower-case-prefix]")]
    public void Load_ValueNotInEncFormat_IsLeftUntouched(string value)
    {
        var path = WriteConfig($$"""{ "Smtp": { "Banner": "{{value}}" } }""");

        Load(path, _protector)["Smtp:Banner"].Should().Be(value);
    }

    // =========================================================================
    // Values that cannot be decrypted (key ring changed, value copied from another machine)
    // =========================================================================

    [Fact]
    public void Load_UndecryptableValue_OptionalFile_ClearsTheValueInsteadOfPassingTheCiphertext()
    {
        var path = WriteConfig($$"""{ "GraphApi": { "ClientSecret": "{{Enc("s3cr3t")}}", "TenantId": "t" } }""");
        var otherKeyRing = new EphemeralDataProtectionProvider().CreateProtector(DataProtectionExtensions.ConfigPurpose);

        var config = Load(path, otherKeyRing, optional: true);

        config["GraphApi:ClientSecret"].Should().BeEmpty(
            "the secret is treated as unconfigured — the ciphertext must never be used as a credential");
        config["GraphApi:TenantId"].Should().Be("t", "the rest of the file still loads");
    }

    [Fact]
    public void Load_UndecryptableValue_RequiredFile_ThrowsAndNamesTheKey()
    {
        var path = WriteConfig($$"""{ "GraphApi": { "ClientSecret": "{{Enc("s3cr3t")}}" } }""");
        var otherKeyRing = new EphemeralDataProtectionProvider().CreateProtector(DataProtectionExtensions.ConfigPurpose);

        var act = () => Load(path, otherKeyRing, optional: false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*GraphApi:ClientSecret*")
            .WithInnerException<CryptographicException>();
    }

    [Fact]
    public void Load_UndecryptableValue_ErrorMessageContainsNeitherCiphertextNorSecret()
    {
        var encrypted = Enc("s3cr3t");
        var path = WriteConfig($$"""{ "GraphApi": { "ClientSecret": "{{encrypted}}" } }""");
        var otherKeyRing = new EphemeralDataProtectionProvider().CreateProtector(DataProtectionExtensions.ConfigPurpose);

        var act = () => Load(path, otherKeyRing, optional: false);

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().NotContain("s3cr3t");
        message.Should().NotContain(encrypted[4..^1], "the message ends up in logs and the event log");
    }
}
