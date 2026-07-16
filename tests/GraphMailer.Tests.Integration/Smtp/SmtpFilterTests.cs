using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace GraphMailer.Tests.Integration.Smtp;

/// <summary>
/// Integration tests for IP whitelist/blacklist filtering and
/// sender/recipient allow-/blocklist filtering.
///
/// All tests use 127.0.0.1 as the client IP (MailKit always connects via
/// IPv4 loopback when targeting "127.0.0.1").  IPv6 logic is covered by the
/// dedicated unit tests in IpFilterServiceTests.
/// </summary>
[Collection("SmtpIntegration")]
public class SmtpFilterTests
{
    // =========================================================================
    // IP whitelist
    // =========================================================================

    [Fact]
    public async Task IpWhitelist_ClientIpInWhitelist_AcceptsMail()
    {
        // 127.0.0.1 is whitelisted → MAIL FROM accepted
        await using var host = await SmtpTestHost.StartAsync(
            ipWhitelist: ["127.0.0.1"]);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage());
        await act.Should().NotThrowAsync(
            "127.0.0.1 is in the IP whitelist and should be accepted");

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task IpWhitelist_ClientIpNotInWhitelist_RejectsMail()
    {
        // Whitelist only permits 10.0.0.1; test client uses 127.0.0.1 → rejected
        await using var host = await SmtpTestHost.StartAsync(
            ipWhitelist: ["10.0.0.1"]);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage());
        await act.Should().ThrowAsync<SmtpCommandException>(
            "127.0.0.1 is not in the IP whitelist; MAIL FROM must be rejected");
    }

    [Fact]
    public async Task IpWhitelist_CidrInWhitelist_AcceptsMail()
    {
        // 127.0.0.0/8 covers 127.0.0.1 → accepted
        await using var host = await SmtpTestHost.StartAsync(
            ipWhitelist: ["127.0.0.0/8"]);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage());
        await act.Should().NotThrowAsync(
            "127.0.0.1 falls inside the whitelisted CIDR 127.0.0.0/8");

        await client.DisconnectAsync(quit: true);
    }

    // =========================================================================
    // IP blacklist
    // =========================================================================

    [Fact]
    public async Task IpBlacklist_ClientIpBlacklisted_RejectsMail()
    {
        // Exact IP blacklisted
        await using var host = await SmtpTestHost.StartAsync(
            ipBlacklist: ["127.0.0.1"]);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage());
        await act.Should().ThrowAsync<SmtpCommandException>(
            "127.0.0.1 is explicitly blacklisted; MAIL FROM must be rejected");
    }

    [Fact]
    public async Task IpBlacklist_CidrCoversClientIp_RejectsMail()
    {
        // CIDR 127.0.0.0/8 covers 127.0.0.1
        await using var host = await SmtpTestHost.StartAsync(
            ipBlacklist: ["127.0.0.0/8"]);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage());
        await act.Should().ThrowAsync<SmtpCommandException>(
            "127.0.0.1 falls inside the blacklisted CIDR 127.0.0.0/8");
    }

    [Fact]
    public async Task IpBlacklist_OtherIpBlacklisted_AcceptsMail()
    {
        // Only 10.0.0.1 is blacklisted; 127.0.0.1 is not affected
        await using var host = await SmtpTestHost.StartAsync(
            ipBlacklist: ["10.0.0.1"]);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage());
        await act.Should().NotThrowAsync(
            "127.0.0.1 is not in the blacklist and should be accepted");

        await client.DisconnectAsync(quit: true);
    }

    // =========================================================================
    // Sender allow-/blocklist
    // =========================================================================

    [Fact]
    public async Task SenderFilter_AllowedSenders_MatchingAddress_AcceptsMail()
    {
        await using var host = await SmtpTestHost.StartAsync(
            allowedSenders: ["sender@example.com"]);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage(from: "sender@example.com"));
        await act.Should().NotThrowAsync(
            "sender@example.com is on the allowed-senders list");

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task SenderFilter_AllowedSenders_NonMatchingAddress_RejectsMail()
    {
        await using var host = await SmtpTestHost.StartAsync(
            allowedSenders: ["allowed@example.com"]);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage(from: "sender@example.com"));
        await act.Should().ThrowAsync<SmtpCommandException>(
            "sender@example.com is not on the allowed-senders list; MAIL FROM must be rejected");
    }

    [Fact]
    public async Task SenderFilter_BlockedSenders_MatchingAddress_RejectsMail()
    {
        await using var host = await SmtpTestHost.StartAsync(
            blockedSenders: ["sender@example.com"]);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage(from: "sender@example.com"));
        await act.Should().ThrowAsync<SmtpCommandException>(
            "sender@example.com is on the blocked-senders list; MAIL FROM must be rejected");
    }

    [Fact]
    public async Task SenderFilter_AllowedDomainWildcard_MatchingSender_AcceptsMail()
    {
        // @example.com wildcard should match sender@example.com
        await using var host = await SmtpTestHost.StartAsync(
            allowedSenders: ["@example.com"]);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage(from: "sender@example.com"));
        await act.Should().NotThrowAsync(
            "sender@example.com matches the @example.com domain wildcard in allowed-senders");

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task SenderFilter_AllowedDomainWildcard_OtherDomain_RejectsMail()
    {
        // @example.com wildcard should NOT match other@other.com
        await using var host = await SmtpTestHost.StartAsync(
            allowedSenders: ["@example.com"]);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage(from: "other@other.com"));
        await act.Should().ThrowAsync<SmtpCommandException>(
            "other@other.com does not match the @example.com domain wildcard; must be rejected");
    }

    // =========================================================================
    // Recipient allow-/blocklist
    // =========================================================================

    [Fact]
    public async Task RecipientFilter_AllowedRecipients_MatchingAddress_AcceptsMail()
    {
        await using var host = await SmtpTestHost.StartAsync(
            allowedRecipients: ["recipient@example.com"]);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage(to: "recipient@example.com"));
        await act.Should().NotThrowAsync(
            "recipient@example.com is on the allowed-recipients list");

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task RecipientFilter_AllowedRecipients_NonMatchingAddress_RejectsMail()
    {
        await using var host = await SmtpTestHost.StartAsync(
            allowedRecipients: ["allowed@example.com"]);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage(to: "recipient@example.com"));
        await act.Should().ThrowAsync<SmtpCommandException>(
            "recipient@example.com is not on the allowed-recipients list; RCPT TO must be rejected");
    }

    [Fact]
    public async Task RecipientFilter_BlockedRecipients_MatchingAddress_RejectsMail()
    {
        await using var host = await SmtpTestHost.StartAsync(
            blockedRecipients: ["recipient@example.com"]);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage(to: "recipient@example.com"));
        await act.Should().ThrowAsync<SmtpCommandException>(
            "recipient@example.com is on the blocked-recipients list; RCPT TO must be rejected");
    }

    // =========================================================================
    // Per-user FromRestrictions (authenticated sessions)
    // =========================================================================

    [Fact]
    public async Task PerUserFromRestrictions_AllowedAddress_AcceptsMail()
    {
        // User "carol" is only allowed to send from carol@example.com
        await using var host = await SmtpTestHost.StartAsync(
            authRequired: true,
            users: [("carol", "pass")],
            perUserFromRestrictions: new Dictionary<string, IEnumerable<string>>
            {
                ["carol"] = ["carol@example.com"]
            });

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);
        await client.AuthenticateAsync("carol", "pass");

        var act = () => client.SendAsync(BuildMessage(from: "carol@example.com"));
        await act.Should().NotThrowAsync(
            "carol@example.com is in carol's FromRestrictions and should be accepted");

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task PerUserFromRestrictions_ForbiddenAddress_RejectsMail()
    {
        // User "carol" is only allowed to send from carol@example.com;
        // sending from other@example.com must be rejected.
        await using var host = await SmtpTestHost.StartAsync(
            authRequired: true,
            users: [("carol", "pass")],
            perUserFromRestrictions: new Dictionary<string, IEnumerable<string>>
            {
                ["carol"] = ["carol@example.com"]
            });

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);
        await client.AuthenticateAsync("carol", "pass");

        var act = () => client.SendAsync(BuildMessage(from: "other@example.com"));
        await act.Should().ThrowAsync<SmtpCommandException>(
            "other@example.com is not in carol's FromRestrictions; MAIL FROM must be rejected");
    }

    [Fact]
    public async Task PerUserFromRestrictions_DomainWildcard_AllowedAddress_AcceptsMail()
    {
        // Wildcard @example.com should allow any address at that domain
        await using var host = await SmtpTestHost.StartAsync(
            authRequired: true,
            users: [("alice", "pass")],
            perUserFromRestrictions: new Dictionary<string, IEnumerable<string>>
            {
                ["alice"] = ["@example.com"]
            });

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);
        await client.AuthenticateAsync("alice", "pass");

        var act = () => client.SendAsync(BuildMessage(from: "alice@example.com"));
        await act.Should().NotThrowAsync(
            "alice@example.com matches the @example.com domain wildcard in FromRestrictions");

        await client.DisconnectAsync(quit: true);
    }

    // =========================================================================
    // Null reverse path (MAIL FROM:<> — NDRs/DSNs per RFC 5321 §4.5.5)
    // =========================================================================

    [Fact]
    public async Task NullReversePath_MailFromEmpty_IsAcceptedAndQueued()
    {
        // MailKit cannot send an empty reverse path, so this test speaks raw SMTP.
        // MailAddressFilter documents that <> must be accepted (bounces/DSNs) — this
        // was the last untested edge case from the 2026-07 audit.
        await using var host = await SmtpTestHost.StartAsync();

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, host.Port);
        using var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        await using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

        (await reader.ReadLineAsync()).Should().StartWith("220");

        await writer.WriteLineAsync("EHLO test.local");
        string? line;
        do { line = await reader.ReadLineAsync(); } while (line is not null && line.StartsWith("250-"));
        line.Should().StartWith("250");

        await writer.WriteLineAsync("MAIL FROM:<>");
        (await reader.ReadLineAsync()).Should().StartWith("250",
            "the null reverse path (bounce/DSN sender) must be accepted");

        await writer.WriteLineAsync("RCPT TO:<recipient@example.com>");
        (await reader.ReadLineAsync()).Should().StartWith("250");

        await writer.WriteLineAsync("DATA");
        (await reader.ReadLineAsync()).Should().StartWith("354");
        await writer.WriteLineAsync("Subject: Delivery status");
        await writer.WriteLineAsync("");
        await writer.WriteLineAsync("This is a bounce body.");
        await writer.WriteLineAsync(".");
        (await reader.ReadLineAsync()).Should().StartWith("250", "the message must be queued");

        await writer.WriteLineAsync("QUIT");

        Directory.GetFiles(host.QueueDirectory, "*.eml").Should().HaveCount(1,
            "the bounce message must land in the queue like any other mail");
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static MimeMessage BuildMessage(
        string from = "sender@example.com",
        string to = "recipient@example.com") =>
        new()
        {
            From = { new MailboxAddress("Sender", from) },
            To = { new MailboxAddress("Recipient", to) },
            Subject = "Filter Integration Test",
            Body = new TextPart("plain") { Text = "Filter test body." },
        };
}
