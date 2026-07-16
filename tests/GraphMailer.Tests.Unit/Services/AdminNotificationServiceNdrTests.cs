using System.Text.Json;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GraphMailer.Tests.Unit.Services;

/// <summary>
/// NDR generation: NDRs are not sent one-shot via Graph but written to the service's
/// own mail queue (with <c>IsNotification = true</c>), so they inherit the queue
/// processor's full retry schedule — a Graph outage at failure time can no longer
/// swallow the NDR silently.
/// </summary>
public sealed class AdminNotificationServiceNdrTests : IDisposable
{
    private readonly IGraphApiClient _graph = Substitute.For<IGraphApiClient>();
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "adminnotify-ndr-tests-" + Guid.NewGuid().ToString("N"));

    public AdminNotificationServiceNdrTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private AdminNotificationService CreateService(
        AdminNotificationsOptions? adminOpts = null,
        NdrOptions? ndrOpts = null)
    {
        var admin = adminOpts ?? new AdminNotificationsOptions
        {
            Enabled = true,
            SenderAddress = "relay@contoso.com",
            RecipientAddresses = ["ops@contoso.com"]
        };
        var ndr = ndrOpts ?? new NdrOptions { Enabled = true, NotifySender = true, NotifyAdmin = false };

        var adminMonitor = Substitute.For<IOptionsMonitor<AdminNotificationsOptions>>();
        adminMonitor.CurrentValue.Returns(admin);

        var ndrMonitor = Substitute.For<IOptionsMonitor<NdrOptions>>();
        ndrMonitor.CurrentValue.Returns(ndr);

        var queueOpts = Substitute.For<IOptionsMonitor<MailQueueOptions>>();
        queueOpts.CurrentValue.Returns(new MailQueueOptions { MailDir = _tempDir });
        var queueWriter = new MailQueueWriter(queueOpts, NullLogger<MailQueueWriter>.Instance);

        return new AdminNotificationService(
            _graph, queueWriter, adminMonitor, ndrMonitor, NullLogger<AdminNotificationService>.Instance);
    }

    private static MailMetadata MakeMeta(string from = "sender@example.com") => new()
    {
        MessageId = "msg-ndr-001",
        From = from,
        To = ["recipient@example.com"],
        Subject = "Important invoice",
        SmtpMessageId = "smtp-001@example.com",
        ReceivedAt = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc)
    };

    private List<MailMetadata> QueuedMetas()
    {
        var queueDir = Path.Combine(_tempDir, "queue");
        if (!Directory.Exists(queueDir)) return [];
        return Directory.GetFiles(queueDir, "*.meta.json")
            .Select(p => JsonSerializer.Deserialize(File.ReadAllText(p), MailMetadataJsonContext.Default.MailMetadata)!)
            .ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendNdrAsync_Disabled_QueuesNothing()
    {
        var svc = CreateService(ndrOpts: new NdrOptions { Enabled = false });
        await svc.SendNdrAsync(MakeMeta(), "Graph API rejected the message");
        QueuedMetas().Should().BeEmpty();
    }

    [Fact]
    public async Task SendNdrAsync_NotifySender_QueuesNdrToOriginalSender()
    {
        var svc = CreateService(
            ndrOpts: new NdrOptions { Enabled = true, NotifySender = true, NotifyAdmin = false });

        await svc.SendNdrAsync(MakeMeta("sender@example.com"), "Mailbox not found");

        var queued = QueuedMetas();
        queued.Should().ContainSingle();
        queued[0].From.Should().Be("relay@contoso.com");
        queued[0].To.Should().Equal("sender@example.com");
        queued[0].Subject.Should().Contain("Undeliverable");
        queued[0].IsNotification.Should().BeTrue("a failed NDR must never generate an NDR for itself");
    }

    [Fact]
    public async Task SendNdrAsync_NotifyAdmin_QueuesNdrToAdminRecipients()
    {
        var svc = CreateService(
            ndrOpts: new NdrOptions { Enabled = true, NotifySender = false, NotifyAdmin = true });

        await svc.SendNdrAsync(MakeMeta(), "Permanent failure");

        var queued = QueuedMetas();
        queued.Should().ContainSingle();
        queued[0].To.Should().Equal("ops@contoso.com");
        queued[0].IsNotification.Should().BeTrue();
    }

    [Fact]
    public async Task SendNdrAsync_BothEnabled_QueuesTwoMails()
    {
        var svc = CreateService(
            ndrOpts: new NdrOptions { Enabled = true, NotifySender = true, NotifyAdmin = true });

        await svc.SendNdrAsync(MakeMeta(), "Rejected");

        QueuedMetas().Should().HaveCount(2);
    }

    [Fact]
    public async Task SendNdrAsync_EmptyFrom_SkipsSenderNdr()
    {
        var svc = CreateService(
            ndrOpts: new NdrOptions { Enabled = true, NotifySender = true, NotifyAdmin = false });

        await svc.SendNdrAsync(MakeMeta(from: ""), "Rejected");

        QueuedMetas().Should().BeEmpty();
    }

    [Fact]
    public async Task SendNdrAsync_FromEqualsAdminSender_SkipsSenderNdrToPreventLoop()
    {
        // The original sender IS the Graph-authorized sender → sending NDR to it would
        // produce an infinite loop if the NDR itself failed. Skip it unconditionally.
        var svc = CreateService(
            ndrOpts: new NdrOptions { Enabled = true, NotifySender = true, NotifyAdmin = false });

        await svc.SendNdrAsync(MakeMeta(from: "relay@contoso.com"), "Rejected");

        QueuedMetas().Should().BeEmpty();
    }

    [Fact]
    public async Task SendNdrAsync_NoSenderAddress_QueuesNothing()
    {
        var svc = CreateService(
            adminOpts: new AdminNotificationsOptions
            {
                SenderAddress = null,
                RecipientAddresses = ["ops@contoso.com"]
            },
            ndrOpts: new NdrOptions { Enabled = true, NotifySender = true });

        await svc.SendNdrAsync(MakeMeta(), "Error");

        QueuedMetas().Should().BeEmpty();
    }

    [Fact]
    public async Task SendNdrAsync_QueuedNdrIsParseableEml()
    {
        // The queued .eml must be a valid MIME message the queue processor can deliver.
        var svc = CreateService(
            ndrOpts: new NdrOptions { Enabled = true, NotifySender = true, NotifyAdmin = false });

        await svc.SendNdrAsync(MakeMeta(), "Mailbox not found");

        var emlPath = Directory.GetFiles(Path.Combine(_tempDir, "queue"), "*.eml").Single();
        var mime = await MimeKit.MimeMessage.LoadAsync(emlPath);
        mime.Subject.Should().Contain("Undeliverable");
        mime.TextBody.Should().Contain("Mailbox not found");
        mime.From.ToString().Should().Contain("relay@contoso.com");
    }
}
