using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace GraphMailer.Tests.Integration.Smtp;

/// <summary>
/// End-to-end SMTP integration tests.
///
/// Each test spins up a real SmtpRelayService on a random free port, sends an
/// actual SMTP connection via MailKit, and asserts the expected outcome.
///
/// Tests in this class run sequentially (xUnit default within a class) so that
/// Directory.SetCurrentDirectory calls inside SmtpTestHost do not race.
/// </summary>
[Collection("SmtpIntegration")]
public class SmtpRelayTests
{
    // -------------------------------------------------------------------------
    // Plain SMTP – authentication NOT required
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PlainSmtp_NoAuthRequired_NoCredentials_AcceptsMail()
    {
        await using var host = await SmtpTestHost.StartAsync(authRequired: false);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage());
        await act.Should().NotThrowAsync("unauthenticated mail should be accepted when auth is not required");

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task PlainSmtp_ValidCredentials_AcceptsMail()
    {
        await using var host = await SmtpTestHost.StartAsync(
            authRequired: false,
            users: [("testuser", "testpass")]);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);
        await client.AuthenticateAsync("testuser", "testpass");

        var act = () => client.SendAsync(BuildMessage());
        await act.Should().NotThrowAsync("authenticated mail with valid credentials should be accepted");

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task PlainSmtp_WrongCredentials_RejectsSubsequentMail()
    {
        await using var host = await SmtpTestHost.StartAsync(
            authRequired: false,
            users: [("testuser", "testpass")]);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        // Wrong password – MailKit throws; session stays open but flagged as Auth:Failed
        await Assert.ThrowsAnyAsync<Exception>(
            () => client.AuthenticateAsync("testuser", "wrongpass"));

        // The Auth:Failed flag must cause MAIL FROM / DATA to be rejected
        var act = () => client.SendAsync(BuildMessage());
        await act.Should().ThrowAsync<SmtpCommandException>(
            "a session with a prior failed auth attempt must not be allowed to send mail");
    }

    [Fact]
    public async Task PlainSmtp_FailedThenSuccessfulAuthOnSameConnection_AcceptsMail()
    {
        // Regression: the Auth:Failed session flag was never cleared, so a client that
        // mistyped its password once and then authenticated correctly on the SAME
        // connection stayed blocked at MAIL FROM for the rest of the session.
        await using var host = await SmtpTestHost.StartAsync(
            authRequired: false,
            users: [("testuser", "testpass")]);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        await Assert.ThrowsAnyAsync<Exception>(
            () => client.AuthenticateAsync("testuser", "wrongpass"));
        await client.AuthenticateAsync("testuser", "testpass");

        var act = () => client.SendAsync(BuildMessage());
        await act.Should().NotThrowAsync(
            "a successful re-authentication on the same connection must clear the failed-auth flag");

        await client.DisconnectAsync(quit: true);
    }

    // -------------------------------------------------------------------------
    // Plain SMTP – authentication required
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PlainSmtp_AuthRequired_NoCredentials_RejectsMail()
    {
        await using var host = await SmtpTestHost.StartAsync(
            authRequired: true,
            users: [("testuser", "testpass")]);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);
        // Intentionally no authentication

        var act = () => client.SendAsync(BuildMessage());
        // MailKit detects via EHLO that the server requires authentication and
        // throws ServiceNotAuthenticatedException before even issuing MAIL FROM.
        await act.Should().ThrowAsync<ServiceNotAuthenticatedException>(
            "server must reject mail when auth is required but no credentials were provided");
    }

    [Fact]
    public async Task PlainSmtp_AuthRequired_ValidCredentials_AcceptsMail()
    {
        await using var host = await SmtpTestHost.StartAsync(
            authRequired: true,
            users: [("testuser", "testpass")]);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);
        await client.AuthenticateAsync("testuser", "testpass");

        var act = () => client.SendAsync(BuildMessage());
        await act.Should().NotThrowAsync("authenticated mail with valid credentials must be accepted even when auth is required");

        await client.DisconnectAsync(quit: true);
    }

    // -------------------------------------------------------------------------
    // IP blocking
    // -------------------------------------------------------------------------

    [Fact]
    public async Task IpBlocking_AfterExceedingFailureThreshold_RejectsMail()
    {
        // Use a very low threshold (3) so the test completes quickly
        await using var host = await SmtpTestHost.StartAsync(
            authRequired: false,
            users: [("testuser", "testpass")],
            ipBlockingEnabled: true,
            ipBlockingThreshold: 3,
            ipBlockingTimeframeSeconds: 60,
            ipBlockingDurationSeconds: 60);

        // Trigger 3 failed auth attempts to exceed the threshold
        for (int i = 0; i < 3; i++)
        {
            using var probe = new SmtpClient();
            await probe.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);
            await Assert.ThrowsAnyAsync<Exception>(
                () => probe.AuthenticateAsync("testuser", "wrongpass"));
            await probe.DisconnectAsync(quit: true);
        }

        // A fresh connection from the same IP must now be blocked
        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage());
        await act.Should().ThrowAsync<SmtpCommandException>(
            "the IP should be blocked after exceeding the failure threshold");
    }

    // -------------------------------------------------------------------------

    private static MimeMessage BuildMessage(
        string from = "sender@example.com",
        string to = "recipient@example.com") =>
        new()
        {
            From = { new MailboxAddress("Sender", from) },
            To = { new MailboxAddress("Recipient", to) },
            Subject = "Integration Test",
            Body = new TextPart("plain") { Text = "Integration test body." },
        };

    /// <summary>
    /// Configures the MailKit SmtpClient to accept self-signed certificates.
    /// Required for all TLS integration tests that use the in-memory test cert.
    /// </summary>
    private static SmtpClient CreateTlsClient()
    {
        var client = new SmtpClient();
        client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        return client;
    }

    // -------------------------------------------------------------------------
    // STARTTLS – authentication NOT required
    // -------------------------------------------------------------------------

    [Fact]
    public async Task StartTls_NoAuthRequired_NoCredentials_AcceptsMail()
    {
        await using var host = await SmtpTestHost.StartAsync(
            mode: "StartTls",
            authRequired: false);

        using var client = CreateTlsClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.StartTls);

        var act = () => client.SendAsync(BuildMessage());
        await act.Should().NotThrowAsync("unauthenticated mail should be accepted on StartTLS when auth is not required");

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task StartTls_ValidCredentials_AcceptsMail()
    {
        await using var host = await SmtpTestHost.StartAsync(
            mode: "StartTls",
            authRequired: false,
            users: [("testuser", "testpass")]);

        using var client = CreateTlsClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.StartTls);
        await client.AuthenticateAsync("testuser", "testpass");

        var act = () => client.SendAsync(BuildMessage());
        await act.Should().NotThrowAsync("authenticated mail over StartTLS with valid credentials should be accepted");

        await client.DisconnectAsync(quit: true);
    }

    // -------------------------------------------------------------------------
    // Implicit TLS (SSL on connect) – authentication NOT required
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ImplicitTls_NoAuthRequired_NoCredentials_AcceptsMail()
    {
        await using var host = await SmtpTestHost.StartAsync(
            mode: "Tls",
            authRequired: false);

        using var client = CreateTlsClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.SslOnConnect);

        var act = () => client.SendAsync(BuildMessage());
        await act.Should().NotThrowAsync("unauthenticated mail should be accepted on implicit TLS when auth is not required");

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task ImplicitTls_ValidCredentials_AcceptsMail()
    {
        await using var host = await SmtpTestHost.StartAsync(
            mode: "Tls",
            authRequired: false,
            users: [("testuser", "testpass")]);

        using var client = CreateTlsClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.SslOnConnect);
        await client.AuthenticateAsync("testuser", "testpass");

        var act = () => client.SendAsync(BuildMessage());
        await act.Should().NotThrowAsync("authenticated mail over implicit TLS with valid credentials should be accepted");

        await client.DisconnectAsync(quit: true);
    }
}
