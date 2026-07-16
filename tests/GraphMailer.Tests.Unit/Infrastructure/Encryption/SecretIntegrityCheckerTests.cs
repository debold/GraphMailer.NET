using GraphMailer.Service.Infrastructure.Encryption;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace GraphMailer.Tests.Unit.Infrastructure.Encryption;

public sealed class SecretIntegrityCheckerTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly ServiceProvider _altSp;
    private readonly IDataProtector _protector;
    private readonly IDataProtector _altProtector;
    private readonly string _keysDir;
    private readonly string _altKeysDir;

    public SecretIntegrityCheckerTests()
    {
        (_sp, _protector, _keysDir) = BuildProtector();
        (_altSp, _altProtector, _altKeysDir) = BuildProtector();
    }

    private static (ServiceProvider, IDataProtector, string) BuildProtector()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"secretcheck-keys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName(DataProtectionExtensions.ApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(dir));
        var sp = services.BuildServiceProvider();
        var protector = sp.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(DataProtectionExtensions.ConfigPurpose);
        return (sp, protector, dir);
    }

    private string Enc(string plain) => $"ENC[{_protector.Protect(plain)}]";

    public void Dispose()
    {
        _sp.Dispose();
        _altSp.Dispose();
        foreach (var d in new[] { _keysDir, _altKeysDir })
            if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
    }

    [Fact]
    public void NoEncryptedValues_ReturnsEmpty()
    {
        var json = """{ "GraphApi": { "TenantId": "abc", "Port": 587 }, "Enabled": true }""";

        var result = SecretIntegrityChecker.FindUndecryptableSecrets(json, _protector);

        result.Should().BeEmpty();
    }

    [Fact]
    public void AllValuesDecryptable_ReturnsEmpty()
    {
        var json = $$"""
        { "GraphApi": { "ClientSecret": "{{Enc("the-secret")}}" } }
        """;

        var result = SecretIntegrityChecker.FindUndecryptableSecrets(json, _protector);

        result.Should().BeEmpty();
    }

    [Fact]
    public void UndecryptableValue_ReturnsItsPath()
    {
        // Encrypted with the primary protector, verified with the alternate (different key ring)
        var json = $$"""
        { "GraphApi": { "ClientSecret": "{{Enc("the-secret")}}" } }
        """;

        var result = SecretIntegrityChecker.FindUndecryptableSecrets(json, _altProtector);

        result.Should().ContainSingle().Which.Should().Be("GraphApi.ClientSecret");
    }

    [Fact]
    public void UndecryptableValueInArray_ReturnsIndexedPath()
    {
        var json = $$"""
        { "Users": [ { "Username": "alice", "Password": "{{Enc("pw")}}" } ] }
        """;

        var result = SecretIntegrityChecker.FindUndecryptableSecrets(json, _altProtector);

        result.Should().ContainSingle().Which.Should().Be("Users[0].Password");
    }

    [Fact]
    public void MixedValidAndInvalid_ReturnsOnlyInvalidPaths()
    {
        // "good" encrypted with alt protector so it stays valid when checked with alt;
        // "bad" encrypted with primary so it fails under alt.
        var good = $"ENC[{_altProtector.Protect("ok")}]";
        var bad = Enc("broken");
        var json = $$"""
        { "GraphApi": { "ClientSecret": "{{good}}" }, "Admin": { "Token": "{{bad}}" } }
        """;

        var result = SecretIntegrityChecker.FindUndecryptableSecrets(json, _altProtector);

        result.Should().ContainSingle().Which.Should().Be("Admin.Token");
    }

    [Fact]
    public void GarbageCipher_IsReportedAsUndecryptable()
    {
        var json = """{ "GraphApi": { "ClientSecret": "ENC[not-real-ciphertext]" } }""";

        var result = SecretIntegrityChecker.FindUndecryptableSecrets(json, _protector);

        result.Should().ContainSingle().Which.Should().Be("GraphApi.ClientSecret");
    }

    [Fact]
    public void NonStringValues_AreIgnored()
    {
        var json = """{ "Port": 587, "Enabled": true, "Timeout": null }""";

        var result = SecretIntegrityChecker.FindUndecryptableSecrets(json, _protector);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Scan_CountsTotalEncryptedValues()
    {
        var json = $$"""
        { "A": "{{Enc("one")}}", "B": { "C": "{{Enc("two")}}" }, "Plain": "not-encrypted" }
        """;

        var scan = SecretIntegrityChecker.Scan(json, _protector);

        scan.TotalEncrypted.Should().Be(2);
        scan.Undecryptable.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ReportsTotalAndFailuresForMixedDocument()
    {
        // Two values encrypted with the primary protector; verified with the alternate,
        // so both count as total and both fail to decrypt.
        var json = $$"""
        { "A": "{{Enc("one")}}", "B": "{{Enc("two")}}" }
        """;

        var scan = SecretIntegrityChecker.Scan(json, _altProtector);

        scan.TotalEncrypted.Should().Be(2);
        scan.Undecryptable.Should().BeEquivalentTo(["A", "B"]);
    }
}
