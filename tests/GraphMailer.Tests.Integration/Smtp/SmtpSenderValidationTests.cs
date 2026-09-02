using GraphMailer.Service.Services;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace GraphMailer.Tests.Integration.Smtp;

/// <summary>
/// End-to-end tests for tenant sender validation at MAIL FROM:
/// a scripted ITenantSenderDirectory drives the SmtpMailboxFilter through the
/// Valid / Unknown / Indeterminate × FailClosed matrix against a live SMTP session.
/// </summary>
[Collection("SmtpIntegration")]
public class SmtpSenderValidationTests
{
    /// <summary>Directory stub returning a fixed result for every address.</summary>
    private sealed class ScriptedDirectory(SenderLookupResult result) : ITenantSenderDirectory
    {
        public Task<SenderLookupResult> ValidateAsync(string address, CancellationToken ct = default)
            => Task.FromResult(result);

        public bool TryResolveGraphUserKey(string address, out string graphUserKey)
        {
            graphUserKey = string.Empty;
            return false;
        }

        public bool TryResolveSender(string address, out string graphUserKey, out TenantRecipientKind kind)
        {
            graphUserKey = string.Empty;
            kind = TenantRecipientKind.Mailbox;
            return false;
        }

        public IReadOnlyList<TenantUser> Recipients() => [];

        public IReadOnlyList<string> MailDomains() => [];

        public Task<SenderDirectoryRefreshResult> RefreshAsync(CancellationToken ct = default)
            => Task.FromResult(new SenderDirectoryRefreshResult(true, 0, 0, null));
    }

    /// <summary>Router stub reporting an explicit route for one address only.</summary>
    private sealed class RoutedSenderRouter(string routedAddress) : ISenderRouter
    {
        public SenderRoute Resolve(string envelopeFrom)
            => new(envelopeFrom, false, null, false, "test");

        public void MarkMailboxUnavailable(string envelopeFrom) { }

        public bool HasExplicitRoute(string envelopeFrom)
            => envelopeFrom.Equals(routedAddress, StringComparison.OrdinalIgnoreCase);
    }

    private static MimeMessage BuildMessage(string from = "sender@corp.com")
    {
        return new MimeMessage
        {
            From = { new MailboxAddress("Sender", from) },
            To = { new MailboxAddress("Recipient", "recipient@example.com") },
            Subject = "Sender Validation Test",
            Body = new TextPart("plain") { Text = "test" },
        };
    }

    [Fact]
    public async Task ValidSender_IsAccepted()
    {
        await using var host = await SmtpTestHost.StartAsync(
            senderValidationEnabled: true,
            senderDirectory: new ScriptedDirectory(SenderLookupResult.Valid));

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage());
        await act.Should().NotThrowAsync("a tenant-known sender must be accepted");

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task UnknownSender_IsRejectedAtMailFrom()
    {
        await using var host = await SmtpTestHost.StartAsync(
            senderValidationEnabled: true,
            senderDirectory: new ScriptedDirectory(SenderLookupResult.Unknown));

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage("ghost@corp.com"));
        await act.Should().ThrowAsync<SmtpCommandException>(
            "a sender that does not exist in the tenant must be rejected with a 5xx at MAIL FROM");

        if (Directory.Exists(host.QueueDirectory))
            Directory.GetFiles(host.QueueDirectory).Should().BeEmpty(
                "nothing must be queued for a rejected sender");

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task IndeterminateValidation_FailOpen_Accepts()
    {
        await using var host = await SmtpTestHost.StartAsync(
            senderValidationEnabled: true,
            senderValidationFailClosed: false,
            senderDirectory: new ScriptedDirectory(SenderLookupResult.Indeterminate));

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage());
        await act.Should().NotThrowAsync(
            "fail-open must accept senders when validation is unavailable");

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task IndeterminateValidation_FailClosed_Rejects()
    {
        await using var host = await SmtpTestHost.StartAsync(
            senderValidationEnabled: true,
            senderValidationFailClosed: true,
            senderDirectory: new ScriptedDirectory(SenderLookupResult.Indeterminate));

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage());
        await act.Should().ThrowAsync<SmtpCommandException>(
            "fail-closed must reject senders when validation is unavailable");

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task ValidationDisabled_UnknownSender_IsAccepted()
    {
        // Even with a directory that would reject, the feature toggle must win.
        await using var host = await SmtpTestHost.StartAsync(
            senderValidationEnabled: false,
            senderDirectory: new ScriptedDirectory(SenderLookupResult.Unknown));

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage("ghost@corp.com"));
        await act.Should().NotThrowAsync(
            "with validation disabled the behavior must be unchanged");

        await client.DisconnectAsync(quit: true);
    }

    // =========================================================================
    // Senders Graph cannot enumerate: mail-enabled public folders,
    // dynamic distribution groups
    // =========================================================================

    [Fact]
    public async Task KnownDomainSender_AcceptMailboxlessEnabled_IsAccepted()
    {
        // A mail-enabled public folder is a real recipient with no Graph representation at all.
        // The only signal left is that its address sits in one of our own verified domains.
        await using var host = await SmtpTestHost.StartAsync(
            senderValidationEnabled: true,
            senderValidationAcceptMailboxless: true,
            senderDirectory: new ScriptedDirectory(SenderLookupResult.KnownDomain));

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage("archive-pf@corp.com"));
        await act.Should().NotThrowAsync(
            "an address in a verified tenant domain must pass when the option is enabled");

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task KnownDomainSender_AcceptMailboxlessDisabled_IsRejected()
    {
        // Default off: without the opt-in, only addresses the directory actually knows pass.
        await using var host = await SmtpTestHost.StartAsync(
            senderValidationEnabled: true,
            senderValidationAcceptMailboxless: false,
            senderDirectory: new ScriptedDirectory(SenderLookupResult.KnownDomain));

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage("archive-pf@corp.com"));
        await act.Should().ThrowAsync<SmtpCommandException>(
            "without the opt-in a directory miss stays a rejection");

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task SenderWithAnExplicitRoute_IsAccepted_EvenWhenTheDirectoryRejects()
    {
        // An explicit route is a deliberate statement that this sender exists and how it is
        // delivered — it must not be second-guessed by a directory that cannot see it.
        await using var host = await SmtpTestHost.StartAsync(
            senderValidationEnabled: true,
            senderDirectory: new ScriptedDirectory(SenderLookupResult.Unknown),
            senderRouter: new RoutedSenderRouter("routed-pf@corp.com"));

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage("routed-pf@corp.com"));
        await act.Should().NotThrowAsync("a sender covered by an explicit route must be accepted");

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task SenderWithoutARoute_IsStillRejected_WhenTheDirectoryRejects()
    {
        await using var host = await SmtpTestHost.StartAsync(
            senderValidationEnabled: true,
            senderDirectory: new ScriptedDirectory(SenderLookupResult.Unknown),
            senderRouter: new RoutedSenderRouter("routed-pf@corp.com"));

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage("ghost@corp.com"));
        await act.Should().ThrowAsync<SmtpCommandException>(
            "the route exemption must apply only to the addresses it covers");

        await client.DisconnectAsync(quit: true);
    }
}
