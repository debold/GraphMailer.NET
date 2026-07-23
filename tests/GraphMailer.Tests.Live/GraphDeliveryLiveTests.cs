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
    public async Task SensitivityReceiptsAndSender_AreAcceptedByGraph()
    {
        // The one genuinely unverifiable-offline part of the fidelity mapping: whether
        // Graph accepts PidTagSensitivity (Integer 0x0036) as an extended property, the
        // two receipt flags, and a Sender that differs from From. A rejection here would
        // hit every relayed message carrying a privacy marking or a receipt request —
        // and the private marking has no other route, Graph's message resource has no
        // sensitivity property at all.
        var s = LiveTestSettings.Current;

        var message = new MimeMessage
        {
            From = { MailboxAddress.Parse(s.SenderAddress!) },
            Sender = MailboxAddress.Parse(s.SenderAddress!),
            To = { MailboxAddress.Parse(s.RecipientAddress!) },
            Subject = $"[LiveTest] sensitivity + receipts {DateTime.UtcNow:HH:mm:ss}",
            Body = new TextPart("plain") { Text = "GraphMailer live test — private marking and receipts" },
        };
        message.Headers.Add("Sensitivity", "Private");
        message.Headers.Add("Disposition-Notification-To", s.SenderAddress!);
        message.Headers.Add("Return-Receipt-To", s.SenderAddress!);

        using var ms = new MemoryStream();
        message.WriteTo(ms);

        var act = () => BuildClient().SendAsync(
            ms.ToArray(), s.SenderAddress!, [s.RecipientAddress!], Guid.NewGuid().ToString("N"), saveToSentItems: false);

        await act.Should().NotThrowAsync(
            "Graph must accept the PidTagSensitivity extended property, both receipt flags and an explicit Sender");
    }

    [LiveFact]
    public async Task ManyCustomHeaders_AreCappedAndStillDelivered()
    {
        // Graph rejects more than five custom internet headers with 400
        // InvalidInternetMessageHeaderCollection, which the queue treats as permanent.
        // Nine headers must therefore still deliver — capped, not rejected.
        var s = LiveTestSettings.Current;

        var message = new MimeMessage
        {
            From = { MailboxAddress.Parse(s.SenderAddress!) },
            To = { MailboxAddress.Parse(s.RecipientAddress!) },
            Subject = $"[LiveTest] header cap {DateTime.UtcNow:HH:mm:ss}",
            Body = new TextPart("plain") { Text = "GraphMailer live test — custom header cap" },
        };
        for (var i = 1; i <= 9; i++)
            message.Headers.Add($"X-GraphMailer-Scan-{i}", $"value-{i}");

        using var ms = new MemoryStream();
        message.WriteTo(ms);

        var act = () => BuildClient().SendAsync(
            ms.ToArray(), s.SenderAddress!, [s.RecipientAddress!], Guid.NewGuid().ToString("N"), saveToSentItems: false);

        await act.Should().NotThrowAsync(
            "nine x-headers must be capped at Graph's limit of five instead of failing the message");
    }

    /// <summary>
    /// Polls the sender's Sent Items for a relayed message by its Message-ID and returns it
    /// with the sensitivity extended property expanded. Exchange writes the copy a moment
    /// after the send call returns, so this waits rather than asserting on the first miss.
    ///
    /// Sent Items rather than the recipient mailbox on purpose: the configured
    /// RecipientAddress is usually outside the test tenant, where the app has no read access.
    /// </summary>
    private static async Task<Microsoft.Graph.Models.Message?> WaitForSentMessageAsync(
        string sender, string internetMessageId, TimeSpan timeout)
    {
        var client = new GraphClientProvider(NullLogger<GraphClientProvider>.Instance)
            .GetClient(LiveTestSettings.Current.ToGraphApiOptions());

        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var page = await client.Users[sender].MailFolders["sentitems"].Messages.GetAsync(cfg =>
            {
                cfg.QueryParameters.Filter = $"internetMessageId eq '{internetMessageId}'";
                cfg.QueryParameters.Expand =
                    ["singleValueExtendedProperties($filter=id eq 'Integer 0x0036')"];
                cfg.QueryParameters.Top = 1;
            });

            if (page?.Value is { Count: > 0 } hits)
                return hits[0];

            if (DateTime.UtcNow >= deadline)
                return null;

            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    [LiveFact]
    public async Task SensitivityAndReplyTo_SurviveExchangeProcessing()
    {
        // The other live tests only prove Graph ACCEPTS the payload at the API boundary.
        // This one reads the message back out of Exchange afterwards, which is a different
        // question: Exchange is known to drop custom headers about a second after a send,
        // so "accepted" is not the same as "kept".
        //
        // Scope: it verifies the copy Exchange committed to the sender's mailbox. It does
        // not verify what an external recipient's mail client renders — the configured
        // RecipientAddress lives outside the test tenant and cannot be read from here.
        var s = LiveTestSettings.Current;
        var messageId = $"livetest-{Guid.NewGuid():N}@graphmailer.test";

        var message = new MimeMessage
        {
            From = { MailboxAddress.Parse(s.SenderAddress!) },
            To = { MailboxAddress.Parse(s.RecipientAddress!) },
            Subject = $"[LiveTest] delivered fidelity {DateTime.UtcNow:HH:mm:ss}",
            MessageId = messageId,
            Body = new TextPart("plain") { Text = "GraphMailer live test — end-to-end fidelity" },
        };
        message.ReplyTo.Add(new MailboxAddress("Support Desk", s.SenderAddress!));
        message.Headers.Add("Sensitivity", "Private");

        using var ms = new MemoryStream();
        message.WriteTo(ms);

        // saveToSentItems: true — the Sent Items copy is what we read back.
        await BuildClient().SendAsync(
            ms.ToArray(), s.SenderAddress!, [s.RecipientAddress!], Guid.NewGuid().ToString("N"),
            saveToSentItems: true);

        var sent = await WaitForSentMessageAsync(
            s.SenderAddress!, $"<{messageId}>", TimeSpan.FromMinutes(2));

        sent.Should().NotBeNull("the relayed message must show up in the sender's Sent Items");

        sent!.ReplyTo.Should().ContainSingle(
            r => r.EmailAddress!.Address!.Equals(s.SenderAddress, StringComparison.OrdinalIgnoreCase),
            "Reply-To must survive Exchange processing, not just the Graph call");

        // Graph echoes the property id normalised: "Integer 0x0036" comes back as
        // "Integer 0x36" (leading zeros stripped). Accept either spelling.
        sent.SingleValueExtendedProperties.Should().Contain(
            p => (p.Id!.EndsWith("0x36", StringComparison.OrdinalIgnoreCase)
               || p.Id!.EndsWith("0x0036", StringComparison.OrdinalIgnoreCase))
              && p.Value == "2",
            "the private marking must still be on the stored message — PidTagSensitivity is " +
            "its only carrier, Graph's message resource has no sensitivity property");
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
