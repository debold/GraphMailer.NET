using FluentAssertions;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using NSubstitute;

namespace GraphMailer.Tests.Unit.Services;

/// <summary>
/// The relay rebuilds every message for Graph, which breaks S/MIME and PGP signatures. The
/// Warning that says so is the operator's only signal, so it must be raised from the
/// content-type alone: MimeKit's MultipartSigned/MultipartEncrypted classes only appear when its
/// cryptography assembly has registered parser hooks, and the detection must not depend on that.
/// </summary>
public sealed class GraphApiClientProtectionWarningTests
{
    private static (GraphApiClient Client, FakeLogger<GraphApiClient> Logger) MakeClient()
    {
        var opts = Substitute.For<IOptionsMonitor<GraphApiOptions>>();
        opts.CurrentValue.Returns(new GraphApiOptions { TenantId = "tenant", ClientId = "client", ClientSecret = "s3cr3t" });
        var smtp = Substitute.For<IOptionsMonitor<SmtpOptions>>();
        smtp.CurrentValue.Returns(new SmtpOptions());
        var logger = new FakeLogger<GraphApiClient>();
        var client = new GraphApiClient(opts, smtp, new GraphClientProvider(NullLogger<GraphClientProvider>.Instance), logger);
        return (client, logger);
    }

    private static void Warn(GraphApiClient client, MimeMessage message)
        => client.WarnAboutUnrelayableContent(message, MimeMessageSplitter.Split(message), "msg-1");

    private static MimeMessage Message(MimeEntity body) => new()
    {
        From = { new MailboxAddress("Sender", "sender@example.com") },
        To = { new MailboxAddress("Recipient", "recipient@example.com") },
        Subject = "Test",
        Body = body,
    };

    [Fact]
    public void Plain_multipart_signed_body_raises_the_warning()
    {
        // A plain Multipart with subtype "signed" — what MimeKit produces when the
        // cryptography hooks are not registered.
        var signed = new Multipart("signed")
        {
            new TextPart("plain") { Text = "Hello" },
            new MimePart("application", "pkcs7-signature") { Content = new MimeContent(new MemoryStream([1, 2, 3])) },
        };
        var (client, logger) = MakeClient();

        Warn(client, Message(signed));

        logger.HasEntry(LogLevel.Warning, "S/MIME/PGP protected").Should().BeTrue();
    }

    [Fact]
    public void Signed_part_nested_in_multipart_mixed_raises_the_warning()
    {
        var signed = new Multipart("signed")
        {
            new TextPart("plain") { Text = "Hello" },
            new MimePart("application", "pgp-signature") { Content = new MimeContent(new MemoryStream([1, 2, 3])) },
        };
        var mixed = new Multipart("mixed") { signed };
        var (client, logger) = MakeClient();

        Warn(client, Message(mixed));

        logger.HasEntry(LogLevel.Warning, "S/MIME/PGP protected").Should().BeTrue();
    }

    [Fact]
    public void Unprotected_message_raises_no_warning()
    {
        var (client, logger) = MakeClient();

        Warn(client, Message(new TextPart("plain") { Text = "Hello" }));

        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }
}
