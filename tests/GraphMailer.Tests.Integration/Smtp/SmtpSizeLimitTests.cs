using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace GraphMailer.Tests.Integration.Smtp;

/// <summary>
/// Integration tests that verify the SMTP size limit enforced by SmtpRelayService.
///
/// SmtpServer advertises the limit as "SIZE &lt;n&gt;" in the EHLO response and
/// rejects any message whose DATA exceeds that limit with a 5xx error.
/// MailKit translates this to a <see cref="SmtpCommandException"/>.
/// </summary>
[Collection("SmtpIntegration")]
public class SmtpSizeLimitTests
{
    // The test server is configured with a 1 KB limit so test data stays small.
    private const int LimitBytes = 1024;

    // =========================================================================
    // Message within the limit
    // =========================================================================

    [Fact]
    public async Task SizeLimit_MessageWithinLimit_AcceptsMail()
    {
        await using var host = await SmtpTestHost.StartAsync(maxSizeBytes: LimitBytes);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        // A minimal message well below 1 KB
        var act = () => client.SendAsync(BuildMessage(bodyBytes: 100));
        await act.Should().NotThrowAsync(
            "a message well below the size limit must be accepted");

        await client.DisconnectAsync(quit: true);
    }

    // =========================================================================
    // Message exceeding the limit
    // =========================================================================

    [Fact]
    public async Task SizeLimit_MessageExceedsLimit_RejectsMail()
    {
        await using var host = await SmtpTestHost.StartAsync(maxSizeBytes: LimitBytes);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        // A message body significantly larger than the 1 KB limit
        var act = () => client.SendAsync(BuildMessage(bodyBytes: LimitBytes * 5));
        await act.Should().ThrowAsync<SmtpCommandException>(
            "a message exceeding the configured MaxSizeBytes must be rejected by the server");

        await client.DisconnectAsync(quit: true);
    }

    // =========================================================================
    // Size limit is advertised in EHLO
    // =========================================================================

    [Fact]
    public async Task SizeLimit_Advertised_InEhloCapabilities()
    {
        await using var host = await SmtpTestHost.StartAsync(maxSizeBytes: LimitBytes);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        // MailKit exposes the SIZE value from EHLO as the server's MaxSize capability
        client.Capabilities.HasFlag(SmtpCapabilities.Size).Should().BeTrue(
            "the server must advertise SIZE in EHLO so clients can pre-check message size");

        client.MaxSize.Should().Be((uint)LimitBytes,
            "the advertised SIZE must match the configured MaxSizeBytes");

        await client.DisconnectAsync(quit: true);
    }

    // =========================================================================
    // Helper
    // =========================================================================

    /// <summary>
    /// Builds a minimal MimeMessage whose body is padded to approximately
    /// <paramref name="bodyBytes"/> bytes.
    /// </summary>
    private static MimeMessage BuildMessage(int bodyBytes)
    {
        var body = new string('x', bodyBytes);
        return new MimeMessage
        {
            From = { new MailboxAddress("Sender", "sender@example.com") },
            To = { new MailboxAddress("Recipient", "recipient@example.com") },
            Subject = "Size Limit Test",
            Body = new TextPart("plain") { Text = body },
        };
    }
}
