using System.Security.Cryptography;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace GraphMailer.Tests.Live;

/// <summary>
/// End-to-end delivery tests against the real Microsoft Graph API of the
/// configured test tenant. All mail stays inside the tenant
/// (SenderAddress → RecipientAddress). Skipped when no tenant is configured.
/// </summary>
public class GraphDeliveryLiveTests
{
    private static GraphApiClient BuildClient()
        => new(
            new StaticOptionsMonitor<GraphApiOptions>(
                LiveTestSettings.Current.ToGraphApiOptions()),
            new GraphClientProvider(NullLogger<GraphClientProvider>.Instance),
            NullLogger<GraphApiClient>.Instance);

    private static byte[] BuildEml(string from, string to, string subject, byte[]? attachment = null)
    {
        var message = new MimeMessage
        {
            From = { MailboxAddress.Parse(from) },
            To = { MailboxAddress.Parse(to) },
            Subject = subject,
        };

        var builder = new BodyBuilder { TextBody = $"GraphMailer live test — {DateTime.UtcNow:O}" };
        if (attachment is not null)
            builder.Attachments.Add("livetest.bin", attachment);
        message.Body = builder.ToMessageBody();

        using var ms = new MemoryStream();
        message.WriteTo(ms);
        return ms.ToArray();
    }

    [LiveFact]
    public async Task SmallMessage_IsDelivered_ViaSendMail()
    {
        var s = LiveTestSettings.Current;
        var eml = BuildEml(s.SenderAddress!, s.RecipientAddress!,
            $"[LiveTest] sendMail {DateTime.UtcNow:HH:mm:ss}");

        // saveToSentItems: true mirrors what the queue does for relayed SMTP mail — this is
        // the only place the Sent Items copy is exercised against a real tenant.
        var act = () => BuildClient().SendAsync(
            eml, s.SenderAddress!, [s.RecipientAddress!], Guid.NewGuid().ToString("N"), saveToSentItems: true);

        await act.Should().NotThrowAsync("a small message must be delivered via the sendMail path");
    }

    [LiveFact]
    public async Task LargeAttachment_IsDelivered_ViaUploadSession()
    {
        // 3.5 MB raw → above the 3 MB threshold, forcing the draft + upload session
        // path. Requires the Mail.ReadWrite application permission.
        var attachment = RandomNumberGenerator.GetBytes(3_500_000);

        var s = LiveTestSettings.Current;
        var eml = BuildEml(s.SenderAddress!, s.RecipientAddress!,
            $"[LiveTest] upload session {DateTime.UtcNow:HH:mm:ss}", attachment);

        var act = () => BuildClient().SendAsync(
            eml, s.SenderAddress!, [s.RecipientAddress!], Guid.NewGuid().ToString("N"), saveToSentItems: false);

        await act.Should().NotThrowAsync(
            "a ≥3 MB attachment must be delivered via draft + upload session " +
            "(requires Mail.ReadWrite — re-run the Entra setup wizard if this fails with 403)");
    }

    [LiveFact]
    public async Task FidelityMessage_WithHeadersImportanceAndInlineImage_IsDelivered()
    {
        // Verifies Graph ACCEPTS the full fidelity mapping in one payload: preserved
        // Message-ID (internetMessageId), In-Reply-To/References (MAPI extended
        // properties 0x1042/0x1039), a custom x-header, high importance, and an
        // inline CID image with IsInline + ContentId. A rejection of any of these
        // would break every relayed message that carries them.
        var s = LiveTestSettings.Current;

        var message = new MimeMessage
        {
            From = { MailboxAddress.Parse(s.SenderAddress!) },
            To = { MailboxAddress.Parse(s.RecipientAddress!) },
            Subject = $"[LiveTest] fidelity {DateTime.UtcNow:HH:mm:ss}",
            MessageId = $"livetest-{Guid.NewGuid():N}@graphmailer.test",
            InReplyTo = $"parent-{Guid.NewGuid():N}@graphmailer.test",
            Importance = MessageImportance.High,
        };
        message.References.Add($"root-{Guid.NewGuid():N}@graphmailer.test");
        message.Headers.Add("X-GraphMailer-LiveTest", "fidelity");

        var builder = new BodyBuilder { HtmlBody = """<p>Inline: <img src="cid:pixel@graphmailer.test"></p>""" };
        // 1x1 transparent PNG
        var pixel = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        var image = builder.LinkedResources.Add("pixel.png", pixel);
        image.ContentId = "pixel@graphmailer.test";
        message.Body = builder.ToMessageBody();

        using var ms = new MemoryStream();
        message.WriteTo(ms);

        var act = () => BuildClient().SendAsync(
            ms.ToArray(), s.SenderAddress!, [s.RecipientAddress!], Guid.NewGuid().ToString("N"), saveToSentItems: false);

        await act.Should().NotThrowAsync(
            "Graph must accept internetMessageId, threading extended properties, x-headers, importance and inline CID attachments");
    }

    [LiveFact]
    public async Task UnknownSender_IsRejected_ByGraph()
    {
        var s = LiveTestSettings.Current;
        var domain = s.SenderAddress![(s.SenderAddress!.IndexOf('@') + 1)..];
        var ghost = $"graphmailer-live-nonexistent@{domain}";
        var eml = BuildEml(ghost, s.RecipientAddress!, "[LiveTest] unknown sender");

        var act = () => BuildClient().SendAsync(
            eml, ghost, [s.RecipientAddress!], Guid.NewGuid().ToString("N"), saveToSentItems: false);

        (await act.Should().ThrowAsync<GraphDeliveryException>(
                "Graph must reject a sender that does not exist in the tenant"))
            .Where(ex => ex.Message.Contains("404") || ex.Message.Contains("ErrorInvalidUser"),
                "the error should surface Graph's 404/ErrorInvalidUser")
            .Where(ex => ex.IsPermanent,
                "a nonexistent sender is a permanent rejection — the queue must fail it fast instead of retrying for 24 h");
    }

    [LiveFact(RequireSenderAlias = true)]
    public async Task AliasSender_IsDelivered_WhenResolvedToUserId()
    {
        // Mirrors what QueueProcessor does: the alias is the From header, but the
        // Graph user key is the resolved mailbox owner (here looked up live).
        var s = LiveTestSettings.Current;
        var gateway = new GraphDirectoryGateway(
            new GraphClientProvider(NullLogger<GraphClientProvider>.Instance),
            new StaticOptionsMonitor<GraphApiOptions>(s.ToGraphApiOptions()));

        var owner = await gateway.FindBySmtpAddressAsync(s.SenderAlias!, CancellationToken.None);
        owner.Should().NotBeNull("the configured SenderAlias must resolve to a tenant user");

        var eml = BuildEml(s.SenderAlias!, s.RecipientAddress!,
            $"[LiveTest] alias sender {DateTime.UtcNow:HH:mm:ss}");

        var act = () => BuildClient().SendAsync(
            eml, owner!.Id, [s.RecipientAddress!], Guid.NewGuid().ToString("N"), saveToSentItems: false);

        await act.Should().NotThrowAsync("sending as the resolved object id must work for aliases");
    }
}
