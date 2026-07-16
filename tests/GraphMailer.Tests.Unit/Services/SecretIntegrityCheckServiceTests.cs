using GraphMailer.Service.Infrastructure.Encryption;
using GraphMailer.Service.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace GraphMailer.Tests.Unit.Services;

public sealed class SecretIntegrityCheckServiceTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly IDataProtectionProvider _provider;
    private readonly IDataProtector _protector;
    private readonly string _keysDir;
    private readonly string _configDir;

    public SecretIntegrityCheckServiceTests()
    {
        _keysDir = Path.Combine(Path.GetTempPath(), $"secretcheck-svc-keys-{Guid.NewGuid():N}");
        _configDir = Path.Combine(Path.GetTempPath(), $"secretcheck-svc-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_keysDir);
        Directory.CreateDirectory(_configDir);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName(DataProtectionExtensions.ApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(_keysDir));
        _sp = services.BuildServiceProvider();
        _provider = _sp.GetRequiredService<IDataProtectionProvider>();
        _protector = _provider.CreateProtector(DataProtectionExtensions.ConfigPurpose);
    }

    public void Dispose()
    {
        _sp.Dispose();
        foreach (var d in new[] { _keysDir, _configDir })
            if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
    }

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_configDir, "graphmailer.json");
        File.WriteAllText(path, json);
        return path;
    }

    private SecretIntegrityCheckService Create(
        string configPath,
        IAdminNotificationService notify,
        FakeLogger<SecretIntegrityCheckService> logger)
        => new(_provider, notify, logger) { ConfigPath = configPath };

    [Fact]
    public async Task AllSecretsDecryptable_NoNotification_LogsInformation()
    {
        var path = WriteConfig($$"""
        { "GraphApi": { "ClientSecret": "ENC[{{_protector.Protect("ok")}}]" } }
        """);
        var notify = Substitute.For<IAdminNotificationService>();
        var logger = new FakeLogger<SecretIntegrityCheckService>();
        var sut = Create(path, notify, logger);

        await sut.RunCheckAsync(CancellationToken.None);

        await notify.DidNotReceive().NotifyConfigDecryptionFailedAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        logger.HasEntry(LogLevel.Information, "decryptable").Should().BeTrue();
    }

    [Fact]
    public async Task UndecryptableSecret_LogsError_AndNotifiesWithFieldPath()
    {
        // Encrypt with a foreign key ring so the service's protector cannot decrypt it.
        var foreign = ForeignEncValue("secret");
        var path = WriteConfig($$"""
        { "GraphApi": { "ClientSecret": "{{foreign}}" } }
        """);
        var notify = Substitute.For<IAdminNotificationService>();
        var logger = new FakeLogger<SecretIntegrityCheckService>();
        var sut = Create(path, notify, logger);

        await sut.RunCheckAsync(CancellationToken.None);

        logger.HasEntry(LogLevel.Error, "cannot be decrypted").Should().BeTrue();
        await notify.Received(1).NotifyConfigDecryptionFailedAsync(
            Arg.Is<IReadOnlyList<string>>(l => l.Count == 1 && l[0] == "GraphApi.ClientSecret"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoConfigFile_NoNotification()
    {
        var notify = Substitute.For<IAdminNotificationService>();
        var logger = new FakeLogger<SecretIntegrityCheckService>();
        var sut = Create(Path.Combine(_configDir, "does-not-exist.json"), notify, logger);

        await sut.RunCheckAsync(CancellationToken.None);

        await notify.DidNotReceive().NotifyConfigDecryptionFailedAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidJson_NoNotification_LogsWarning()
    {
        var path = WriteConfig("{ this is not json");
        var notify = Substitute.For<IAdminNotificationService>();
        var logger = new FakeLogger<SecretIntegrityCheckService>();
        var sut = Create(path, notify, logger);

        await sut.RunCheckAsync(CancellationToken.None);

        await notify.DidNotReceive().NotifyConfigDecryptionFailedAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        logger.HasEntry(LogLevel.Warning, "not valid JSON").Should().BeTrue();
    }

    private static string ForeignEncValue(string plain)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"secretcheck-foreign-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var services = new ServiceCollection();
            services.AddDataProtection()
                .SetApplicationName(DataProtectionExtensions.ApplicationName)
                .PersistKeysToFileSystem(new DirectoryInfo(dir));
            using var sp = services.BuildServiceProvider();
            var protector = sp.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector(DataProtectionExtensions.ConfigPurpose);
            return $"ENC[{protector.Protect(plain)}]";
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
