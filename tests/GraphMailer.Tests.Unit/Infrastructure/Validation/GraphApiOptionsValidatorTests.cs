using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GraphMailer.Tests.Unit.Infrastructure.Validation;

public sealed class GraphApiOptionsValidatorTests
{
    // =========================================================================
    // Nothing configured → Warning + Success (SMTP intake still works)
    // =========================================================================

    [Fact]
    public void Validate_NothingConfigured_Succeeds()
    {
        var sut = BuildSut();
        var result = sut.Validate(null, new GraphApiOptions());

        result.Succeeded.Should().BeTrue("unconfigured Graph API should not prevent service startup");
    }

    [Fact]
    public void Validate_NothingConfigured_LogsWarning()
    {
        var logger = new FakeLogger<GraphApiOptionsValidator>();
        var sut = new GraphApiOptionsValidator(logger);

        sut.Validate(null, new GraphApiOptions());

        logger.HasEntry(Microsoft.Extensions.Logging.LogLevel.Warning, "Graph API credentials are not configured")
            .Should().BeTrue();
    }

    // =========================================================================
    // Fully configured → Success
    // =========================================================================

    [Fact]
    public void Validate_WithClientSecret_Succeeds()
    {
        var opts = new GraphApiOptions
        {
            TenantId = "11111111-1111-1111-1111-111111111111",
            ClientId = "22222222-2222-2222-2222-222222222222",
            ClientSecret = "s3cr3t"
        };

        BuildSut().Validate(null, opts).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithCertificateThumbprint_Succeeds()
    {
        var opts = new GraphApiOptions
        {
            TenantId = "11111111-1111-1111-1111-111111111111",
            ClientId = "22222222-2222-2222-2222-222222222222",
            ClientCertificateThumbprint = "A1B2C3D4E5F6A1B2C3D4E5F6A1B2C3D4E5F6A1B2"
        };

        BuildSut().Validate(null, opts).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithCertificateSubjectName_Succeeds()
    {
        var opts = new GraphApiOptions
        {
            TenantId = "11111111-1111-1111-1111-111111111111",
            ClientId = "22222222-2222-2222-2222-222222222222",
            ClientCertificateSubjectName = "graphmailer.contoso.com"
        };

        BuildSut().Validate(null, opts).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_BothSecretAndCert_SucceedsWithWarning()
    {
        var logger = new FakeLogger<GraphApiOptionsValidator>();
        var sut = new GraphApiOptionsValidator(logger);

        var opts = new GraphApiOptions
        {
            TenantId = "11111111-1111-1111-1111-111111111111",
            ClientId = "22222222-2222-2222-2222-222222222222",
            ClientSecret = "s3cr3t",
            ClientCertificateSubjectName = "graphmailer.contoso.com"
        };

        var result = sut.Validate(null, opts);

        result.Succeeded.Should().BeTrue("both auth methods configured is not a fatal error");
        logger.HasEntry(Microsoft.Extensions.Logging.LogLevel.Warning, "certificate will be used")
            .Should().BeTrue();
    }

    // =========================================================================
    // Partial configuration → Fail
    // =========================================================================

    [Fact]
    public void Validate_MissingTenantId_Fails()
    {
        var opts = new GraphApiOptions
        {
            ClientId = "22222222-2222-2222-2222-222222222222",
            ClientSecret = "s3cr3t"
        };

        var result = BuildSut().Validate(null, opts);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("TenantId");
    }

    [Fact]
    public void Validate_MissingClientId_Fails()
    {
        var opts = new GraphApiOptions
        {
            TenantId = "11111111-1111-1111-1111-111111111111",
            ClientSecret = "s3cr3t"
        };

        var result = BuildSut().Validate(null, opts);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("ClientId");
    }

    [Fact]
    public void Validate_MissingClientSecret_Fails()
    {
        var opts = new GraphApiOptions
        {
            TenantId = "11111111-1111-1111-1111-111111111111",
            ClientId = "22222222-2222-2222-2222-222222222222"
        };

        var result = BuildSut().Validate(null, opts);

        result.Failed.Should().BeTrue();
        // Error message must mention at least one of the missing auth-method fields
        (result.FailureMessage!.Contains("ClientSecret") ||
         result.FailureMessage.Contains("ClientCertificateSubjectName")).Should().BeTrue();
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static GraphApiOptionsValidator BuildSut() =>
        new(NullLogger<GraphApiOptionsValidator>.Instance);
}
