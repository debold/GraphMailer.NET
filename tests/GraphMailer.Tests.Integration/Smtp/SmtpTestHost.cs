using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Certificates;
using GraphMailer.Service.Infrastructure.Encryption;
using GraphMailer.Service.Infrastructure.Metrics;
using GraphMailer.Service.Infrastructure.Security;
using GraphMailer.Service.Infrastructure.Smtp;
using GraphMailer.Service.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SmtpServer.Authentication;
using SmtpServer.Storage;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace GraphMailer.Tests.Integration.Smtp;

/// <summary>
/// Builds a minimal IHost with SmtpRelayService for integration testing.
/// Each instance listens on a randomly assigned free port and writes queued
/// messages to a temporary directory.
///
/// Dispose (await using) to stop the host and clean up the temp directory.
/// </summary>
internal sealed class SmtpTestHost : IAsyncDisposable
{
    public int Port { get; }
    public string QueueDirectory { get; }

    /// <summary>The NSubstitute IMetricsService the host runs with — assert Received() calls on it.</summary>
    public IMetricsService Metrics => _host.Services.GetRequiredService<IMetricsService>();

    private readonly IHost _host;
    private readonly string _workDir;
    private readonly string _originalDirectory;
    private readonly X509Certificate2? _ownedCertificate; // disposed with this instance

    private SmtpTestHost(int port, string workDir, IHost host, string originalDir,
        X509Certificate2? ownedCertificate = null)
    {
        Port = port;
        _workDir = workDir;
        QueueDirectory = Path.Combine(workDir, "mail", "queue");
        _host = host;
        _originalDirectory = originalDir;
        _ownedCertificate = ownedCertificate;
    }

    /// <summary>
    /// Builds, starts, and returns an SmtpTestHost.
    /// The host is ready to accept connections when this method returns.
    /// </summary>
    public static async Task<SmtpTestHost> StartAsync(
        string mode = "Plain",
        bool authRequired = false,
        IEnumerable<(string Username, string Password)>? users = null,
        bool ipBlockingEnabled = false,
        int ipBlockingThreshold = 10,
        int ipBlockingTimeframeSeconds = 600,
        int ipBlockingDurationSeconds = 600,
        IEnumerable<string>? ipWhitelist = null,
        IEnumerable<string>? ipBlacklist = null,
        IEnumerable<string>? allowedSenders = null,
        IEnumerable<string>? blockedSenders = null,
        IEnumerable<string>? allowedRecipients = null,
        IEnumerable<string>? blockedRecipients = null,
        IReadOnlyDictionary<string, IEnumerable<string>>? perUserFromRestrictions = null,
        bool withCertificate = true,
        bool tlsFailClosed = false,
        bool waitForListener = true,
        long? maxSizeBytes = null,
        bool senderValidationEnabled = false,
        bool senderValidationFailClosed = false,
        ITenantSenderDirectory? senderDirectory = null,
        ILoggerProvider? loggerProvider = null)
    {
        var port = GetFreePort();
        var workDir = Path.Combine(Path.GetTempPath(), $"gm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        var originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(workDir);

        var config = new Dictionary<string, string?>
        {
            // Without this, MailQueueWriter falls back to AppPaths.MailDir and the
            // tests would write into the machine's REAL ProgramData queue directory.
            ["MailQueue:MailDir"] = Path.Combine(workDir, "mail"),
            ["Servers:0:Name"] = "TestServer",
            ["Servers:0:Port"] = port.ToString(),
            ["Servers:0:Mode"] = mode,
            ["Servers:0:AuthRequired"] = authRequired ? "true" : "false",
            ["Smtp:Banner"] = "test.local",
            ["Smtp:MaxSizeBytes"] = (maxSizeBytes ?? 10_485_760).ToString(),
            ["Certificate:FailClosed"] = tlsFailClosed ? "true" : "false",
            ["IpBlockingProtection:Enabled"] = ipBlockingEnabled ? "true" : "false",
            ["IpBlockingProtection:FailureThreshold"] = ipBlockingThreshold.ToString(),
            ["IpBlockingProtection:TimeframeSeconds"] = ipBlockingTimeframeSeconds.ToString(),
            ["IpBlockingProtection:BlockDurationSeconds"] = ipBlockingDurationSeconds.ToString(),
            ["SenderValidation:Enabled"] = senderValidationEnabled ? "true" : "false",
            ["SenderValidation:FailClosed"] = senderValidationFailClosed ? "true" : "false",
        };

        int i = 0;
        foreach (var (username, password) in users ?? [])
        {
            config[$"Users:{i}:Username"] = username;
            config[$"Users:{i}:Password"] = password;

            if (perUserFromRestrictions?.TryGetValue(username, out var restrictions) == true)
            {
                int j = 0;
                foreach (var r in restrictions)
                    config[$"Users:{i}:FromRestrictions:{j++}"] = r;
            }

            i++;
        }

        AddListToConfig(config, "IpWhitelist", ipWhitelist);
        AddListToConfig(config, "IpBlacklist", ipBlacklist);
        AddListToConfig(config, "AllowedSenders", allowedSenders);
        AddListToConfig(config, "BlockedSenders", blockedSenders);
        AddListToConfig(config, "AllowedRecipients", allowedRecipients);
        AddListToConfig(config, "BlockedRecipients", blockedRecipients);

        var keysDir = new DirectoryInfo(Path.Combine(workDir, "keys"));
        keysDir.Create();

        // For TLS/StartTLS modes, generate an in-memory self-signed certificate.
        // Pass withCertificate: false to simulate a missing certificate (cert not found
        // in the Windows Certificate Store), which causes SmtpRelayService to fall
        // back to plain SMTP and emit a warning log.
        X509Certificate2? ownedCert = null;
        ICertificateLoader certLoader;
        if (!mode.Equals("Plain", StringComparison.OrdinalIgnoreCase) && withCertificate)
        {
            ownedCert = CreateTestCertificate();
            certLoader = new TestCertificateLoader(ownedCert);
        }
        else
        {
            certLoader = new TestCertificateLoader(null);
        }

        var host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c =>
            {
                c.Sources.Clear();
                c.AddInMemoryCollection(config);
            })
            .ConfigureLogging(l =>
            {
                l.ClearProviders();
                if (loggerProvider is not null)
                    l.AddProvider(loggerProvider);
            })
            .ConfigureServices((ctx, services) =>
            {
                services.Configure<List<SmtpServerEntry>>(ctx.Configuration.GetSection("Servers"));
                services.Configure<SmtpOptions>(ctx.Configuration.GetSection("Smtp"));
                services.Configure<MailQueueOptions>(ctx.Configuration.GetSection(MailQueueOptions.SectionName));
                services.Configure<CertificateOptions>(ctx.Configuration.GetSection("Certificate"));
                services.Configure<IpBlockingProtectionOptions>(ctx.Configuration.GetSection("IpBlockingProtection"));
                services.Configure<SenderValidationOptions>(ctx.Configuration.GetSection(SenderValidationOptions.SectionName));
                services.Configure<SmtpAccessOptions>(ctx.Configuration);

                services.AddDataProtection()
                    .SetApplicationName(DataProtectionExtensions.ApplicationName)
                    .PersistKeysToFileSystem(keysDir);

                // Inject the test certificate loader so TLS tests work without
                // touching the Windows Certificate Store.
                services.AddSingleton<ICertificateLoader>(_ => certLoader);

                services.AddSingleton<IpBlockingService>();
                services.AddSingleton<AuthHandler>();
                services.AddSingleton<IPasswordCaptureService, PasswordCaptureService>();
                // Permissive by default; tests can inject a scripted directory to
                // exercise tenant sender validation end-to-end.
                services.AddSingleton(senderDirectory ?? new PermissiveSenderDirectory());
                services.AddSingleton<IUserAuthenticator, SmtpUserAuthenticator>();
                services.AddSingleton<IMailboxFilter, SmtpMailboxFilter>();
                services.AddSingleton<IMessageStore, SmtpMessageStore>();
                services.AddSingleton<MailQueueWriter>();
                services.AddSingleton<PortProbeRegistry>();
                services.AddSingleton<IMetricsService>(_ => Substitute.For<IMetricsService>());
                services.AddHostedService<SmtpRelayService>();
            })
            .Build();

        await host.StartAsync();
        // Fail-closed tests expect NO listener to start — skip the readiness wait there.
        if (waitForListener)
            await WaitForPortAsync(port);

        return new SmtpTestHost(port, workDir, host, originalDir, ownedCert);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();

        Directory.SetCurrentDirectory(_originalDirectory);

        _ownedCertificate?.Dispose();

        try { Directory.Delete(_workDir, recursive: true); }
        catch { /* best effort – temp files cleaned up by OS eventually */ }
    }

    // -------------------------------------------------------------------------

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForPortAsync(int port, int maxWaitMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(maxWaitMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch
            {
                await Task.Delay(50);
            }
        }
        throw new TimeoutException(
            $"SMTP server did not start listening on port {port} within {maxWaitMs} ms.");
    }

    /// <summary>
    /// Creates an in-memory self-signed certificate for localhost / 127.0.0.1.
    /// The caller is responsible for disposing the returned instance.
    /// </summary>
    internal static X509Certificate2 CreateTestCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // id-kp-serverAuth

        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        req.CertificateExtensions.Add(san.Build());

        using var temp = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));

        // Export to PFX and reimport so the private key is fully embedded and
        // not tied to the RSA object's lifetime.
        // Note: EphemeralKeySet is intentionally NOT used here – Windows Schannel
        // (the SslStream backend on Windows) cannot access ephemeral keys.
        // The default key storage writes a temporary CNG container to the current
        // user's key store; it is cleaned up when the X509Certificate2 is disposed.
        var pfx = temp.Export(X509ContentType.Pfx);
        return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.Exportable);
    }

    // -------------------------------------------------------------------------

    private static void AddListToConfig(
        Dictionary<string, string?> config,
        string key,
        IEnumerable<string>? values)
    {
        if (values is null) return;
        int i = 0;
        foreach (var v in values)
            config[$"{key}:{i++}"] = v;
    }

    // -------------------------------------------------------------------------

    /// <summary>
    /// ICertificateLoader that returns a fixed in-memory certificate (or null).
    /// Used by SmtpTestHost to bypass the Windows Certificate Store.
    /// </summary>
    private sealed class TestCertificateLoader(X509Certificate2? cert) : ICertificateLoader
    {
        public X509Certificate2? LoadCertificate() => cert;
        public bool IsConfigured() => cert is not null;
    }

    /// <summary>Default ITenantSenderDirectory: accepts every sender, resolves nothing.</summary>
    private sealed class PermissiveSenderDirectory : ITenantSenderDirectory
    {
        public Task<SenderLookupResult> ValidateAsync(string address, CancellationToken ct = default)
            => Task.FromResult(SenderLookupResult.Valid);

        public bool TryResolveGraphUserKey(string address, out string graphUserKey)
        {
            graphUserKey = string.Empty;
            return false;
        }

        public Task<SenderDirectoryRefreshResult> RefreshAsync(CancellationToken ct = default)
            => Task.FromResult(new SenderDirectoryRefreshResult(true, 0, 0, null));
    }
}
