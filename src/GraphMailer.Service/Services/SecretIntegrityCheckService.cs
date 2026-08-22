using System.Text.Json;
using GraphMailer.Service.Infrastructure;
using GraphMailer.Service.Infrastructure.Encryption;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GraphMailer.Service.Services;

/// <summary>
/// One-shot startup check that verifies every <c>ENC[...]</c> value in
/// <c>graphmailer.json</c> can be decrypted with the current Data Protection key ring.
///
/// The configuration provider loads optional config with <c>optional: true</c>, so an
/// undecryptable secret is silently blanked and the service starts with, e.g., an empty
/// Graph client secret — the failure would otherwise only surface at the first Graph call
/// or SMTP login. This check turns that silent gap into an eager <see cref="LogLevel.Error"/>
/// plus an admin notification.
/// </summary>
internal sealed class SecretIntegrityCheckService : BackgroundService
{
    private readonly IDataProtectionProvider _dataProtection;
    private readonly IAdminNotificationService _notify;
    private readonly ILogger<SecretIntegrityCheckService> _logger;

    public SecretIntegrityCheckService(
        IDataProtectionProvider dataProtection,
        IAdminNotificationService notify,
        ILogger<SecretIntegrityCheckService> logger)
    {
        _dataProtection = dataProtection;
        _notify = notify;
        _logger = logger;
    }

    /// <summary>
    /// Config file to inspect. Defaults to <see cref="AppPaths.ConfigFilePath"/>; overridable
    /// in tests because <see cref="AppPaths"/> caches its base directory at static init.
    /// </summary>
    internal string ConfigPath { get; init; } = AppPaths.ConfigFilePath;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunCheckAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // service shutting down – nothing to report
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SecretCheck] Unexpected error during secret integrity check");
        }
    }

    // internal so unit tests can drive the check without the hosting infrastructure
    internal async Task RunCheckAsync(CancellationToken ct)
    {
        var path = ConfigPath;
        if (!File.Exists(path))
        {
            _logger.LogDebug("[SecretCheck] No user config at {Path} – no secrets to verify", path);
            return;
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, ct);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "[SecretCheck] Cannot read config file {Path} – skipping secret check", path);
            return;
        }

        SecretIntegrityChecker.SecretScanResult scan;
        try
        {
            var protector = _dataProtection.CreateProtector(DataProtectionExtensions.ConfigPurpose);
            scan = SecretIntegrityChecker.Scan(json, protector);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[SecretCheck] Config file {Path} is not valid JSON – skipping secret check", path);
            return;
        }

        // Secrets left in plaintext. The runtime accepts them — that is what makes initial setup
        // possible — but nothing turns them into ENC[...] on its own, so without this line they
        // stay readable in graphmailer.json and in every copy or backup of it, with no signal at
        // all. Warning rather than Error: the service works fine, the protection just is not on.
        if (scan.Plaintext.Count > 0)
        {
            _logger.LogWarning(
                "[SecretCheck] {Count} config secret(s) are stored in plaintext, not as ENC[...]: {Fields}. " +
                "Anyone who can read graphmailer.json — or a backup or copy of it — can read these values. " +
                "Re-enter them in the ConfigTool and save to encrypt them with the Data Protection key ring.",
                scan.Plaintext.Count, string.Join(", ", scan.Plaintext));
        }

        var failing = scan.Undecryptable;
        if (failing.Count == 0)
        {
            _logger.LogInformation(
                "[SecretCheck] All encrypted config secrets are decryptable with the current Data Protection key ring");
            return;
        }

        _logger.LogError(
            "[SecretCheck] {Count} encrypted config value(s) cannot be decrypted with the current Data Protection key ring: {Fields}. " +
            "The key ring (HKLM\\SOFTWARE\\GraphMailer\\DataProtection) may differ from the one that encrypted graphmailer.json " +
            "(e.g. config restored to a different machine). Re-enter the affected values in the ConfigTool to re-encrypt them.",
            failing.Count, string.Join(", ", failing));

        await _notify.NotifyConfigDecryptionFailedAsync(failing, ct);
    }
}
