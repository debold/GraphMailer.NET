using GraphMailer.Service.Configuration;
using Microsoft.Extensions.Options;

namespace GraphMailer.Service.Infrastructure.Validation;

/// <summary>
/// Validates Graph API options at startup.
/// Missing credentials are a warning (not a fatal error) because the SMTP intake
/// works without them – only the queue processor requires them.
/// Partial configuration (e.g. TenantId without ClientId) is a hard failure.
/// </summary>
internal sealed class GraphApiOptionsValidator : IValidateOptions<GraphApiOptions>
{
    private readonly ILogger<GraphApiOptionsValidator> _logger;

    public GraphApiOptionsValidator(ILogger<GraphApiOptionsValidator> logger)
    {
        _logger = logger;
    }

    public ValidateOptionsResult Validate(string? name, GraphApiOptions options)
    {
        bool hasAnyField =
            !string.IsNullOrWhiteSpace(options.TenantId) ||
            !string.IsNullOrWhiteSpace(options.ClientId) ||
            options.HasClientSecret ||
            options.HasClientCertificate;

        if (!hasAnyField)
        {
            _logger.LogWarning(
                "[GraphApi] Graph API credentials are not configured. " +
                "The SMTP server will accept and queue messages, but delivery via " +
                "Microsoft Graph API will not work until TenantId, ClientId and " +
                "either ClientSecret or ClientCertificateSubjectName are set in config/graphmailer.json.");

            // Not a hard failure – SMTP intake works independently
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.TenantId))
            return ValidateOptionsResult.Fail("[GraphApi] TenantId is required when Graph API is configured.");

        if (string.IsNullOrWhiteSpace(options.ClientId))
            return ValidateOptionsResult.Fail("[GraphApi] ClientId is required when Graph API is configured.");

        if (!options.HasClientSecret && !options.HasClientCertificate)
            return ValidateOptionsResult.Fail(
                "[GraphApi] Either ClientSecret or ClientCertificateSubjectName must be set.");

        if (options.HasClientSecret && options.HasClientCertificate)
        {
            _logger.LogWarning(
                "[GraphApi] Both ClientSecret and ClientCertificateSubjectName are configured. " +
                "The certificate will be used; ClientSecret is ignored.");
        }

        return ValidateOptionsResult.Success;
    }
}
