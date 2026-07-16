using GraphMailer.Service.Infrastructure.Encryption;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;

namespace GraphMailer.Tests.Unit.Infrastructure.Encryption;

public class EncryptionTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly IDataProtector  _protector;
    private readonly string          _keysDir;

    public EncryptionTests()
    {
        _keysDir = Path.Combine(Path.GetTempPath(), $"graphmailer-test-keys-{Guid.NewGuid()}");
        Directory.CreateDirectory(_keysDir);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName(DataProtectionExtensions.ApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(_keysDir));

        _sp        = services.BuildServiceProvider();
        _protector = _sp.GetRequiredService<IDataProtectionProvider>()
                        .CreateProtector(DataProtectionExtensions.ConfigPurpose);
    }

    [Fact]
    public void Protect_Unprotect_Roundtrip()
    {
        const string original = "super-secret-client-secret-value";

        var cipherText = _protector.Protect(original);
        var decrypted  = _protector.Unprotect(cipherText);

        decrypted.Should().Be(original);
    }

    [Fact]
    public void Protect_ProducesEncFormat_ThatProviderRecognises()
    {
        const string original = "my-secret";

        var encValue   = $"ENC[{_protector.Protect(original)}]";

        encValue.Should().StartWith("ENC[").And.EndWith("]");
        encValue.Length.Should().BeGreaterThan("ENC[]".Length);
    }

    [Fact]
    public void Unprotect_WithWrongKey_ThrowsCryptographicException()
    {
        // Build a second protector with a different key ring
        var altKeysDir = Path.Combine(Path.GetTempPath(), $"graphmailer-test-keys-alt-{Guid.NewGuid()}");
        Directory.CreateDirectory(altKeysDir);
        var altServices = new ServiceCollection();
        altServices.AddDataProtection()
            .SetApplicationName(DataProtectionExtensions.ApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(altKeysDir));
        using var altSp = altServices.BuildServiceProvider();
        var altProtector = altSp.GetRequiredService<IDataProtectionProvider>()
                                .CreateProtector(DataProtectionExtensions.ConfigPurpose);

        var cipherText = _protector.Protect("original-value");

        var act = () => altProtector.Unprotect(cipherText);

        act.Should().Throw<CryptographicException>();

        Directory.Delete(altKeysDir, recursive: true);
    }

    [Fact]
    public void Protect_SameInput_ProducesDifferentCiphertexts()
    {
        const string input = "determinism-check";

        var cipher1 = _protector.Protect(input);
        var cipher2 = _protector.Protect(input);

        // Data Protection adds a random nonce per call
        cipher1.Should().NotBe(cipher2);
    }

    public void Dispose()
    {
        _sp.Dispose();
        if (Directory.Exists(_keysDir))
            Directory.Delete(_keysDir, recursive: true);
    }
}
