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

        public Task<SenderDirectoryRefreshResult> RefreshAsync(CancellationToken ct = default)
            => Task.FromResult(new SenderDirectoryRefreshResult(true, 0, 0, null));
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
}
