using GraphMailer.Service.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GraphMailer.Service.Infrastructure.Validation;

/// <summary>
/// Validates SMTP options at startup.
///
/// Exchange Online / Graph API size constraints:
///   • Exchange Online hard maximum per message: 150 MB (organisational default ~35 MB)
///   • Graph API /me/sendMail endpoint: 4 MB total request body (≈ 3 MB raw message)
///   • Graph API createUploadSession: up to 150 MB
///
/// The Phase 3 queue processor will route messages automatically:
///   ≤ ~3 MB raw → sendMail (single POST)
///   > ~3 MB raw → createUploadSession + streaming upload
///
/// Validation rules:
///   • MaxSizeBytes ≤ 0 → startup failure (misconfiguration)
///   • MaxSizeBytes > 150 MB → startup warning (messages will be undeliverable)
/// </summary>
internal sealed class SmtpOptionsValidator : IValidateOptions<SmtpOptions>
{
    /// <summary>Exchange Online absolute hard maximum (150 MB).</summary>
    internal const long ExchangeOnlineMaxBytes = 150L * 1024 * 1024; // 157,286,400

    private readonly ILogger<SmtpOptionsValidator> _logger;

    public SmtpOptionsValidator(ILogger<SmtpOptionsValidator> logger)
    {
        _logger = logger;
    }

    public ValidateOptionsResult Validate(string? name, SmtpOptions options)
    {
        if (options.MaxSizeBytes <= 0)
            return ValidateOptionsResult.Fail(
                $"Smtp.MaxSizeBytes must be a positive value (configured: {options.MaxSizeBytes} bytes). " +
                "Set it to at least 1 024 bytes.");

        if (options.MaxSizeBytes > ExchangeOnlineMaxBytes)
            _logger.LogWarning(
                "[Smtp] MaxSizeBytes ({Configured:N0} B, {ConfiguredMb:F0} MB) exceeds Exchange Online's " +
                "hard limit of {Limit:N0} B (150 MB). Messages larger than 150 MB cannot be " +
                "delivered via Microsoft Graph API and will remain stuck in the queue indefinitely.",
                options.MaxSizeBytes, options.MaxSizeBytes / 1_048_576.0, ExchangeOnlineMaxBytes);

        return ValidateOptionsResult.Success;
    }
}
