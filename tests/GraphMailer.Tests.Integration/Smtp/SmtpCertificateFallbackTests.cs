using FluentAssertions;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace GraphMailer.Tests.Integration.Smtp;

/// <summary>
/// Integration tests that verify the behaviour of SmtpRelayService when
/// a TLS certificate cannot be found (e.g. the configured subject name
/// is not present in the Windows Certificate Store).
///
/// Current behaviour (implemented in SmtpRelayService.BuildServer):
///   • SmtpRelayService logs a Warning and falls back to plain SMTP.
///   • The endpoint starts and accepts plain connections.
///   • TLS-requiring clients cannot complete the handshake / upgrade.
///
/// Security note – see SmtpRelayService.cs for the intentional trade-off:
/// the fallback ensures the service stays operational even when a cert is
/// missing, but operators must ensure TLS is properly configured in production.
/// With Certificate.FailClosed = true the fallback is disabled: the listener is
/// not started at all (fail-closed tests below).
/// </summary>
[Collection("SmtpIntegration")]
public class SmtpCertificateFallbackTests
{
    // =========================================================================
    // STARTTLS mode – missing certificate
    // =========================================================================

    [Fact]
    public async Task StartTls_MissingCertificate_TlsRequiringClient_CannotConnect()
    {
        // Server is configured for StartTLS but no certificate is available
        await using var host = await SmtpTestHost.StartAsync(
            mode: "StartTls",
            withCertificate: false);

        // A client that REQUIRES StartTLS looks for "STARTTLS" in EHLO capabilities.
        // Without a certificate the server does not advertise STARTTLS →
        // MailKit throws during ConnectAsync.
        using var client = new SmtpClient();
        client.ServerCertificateValidationCallback = (_, _, _, _) => true;

        var act = () => client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.StartTls);

        await act.Should().ThrowAsync<Exception>(
            "the server does not advertise STARTTLS when no certificate is available; " +
            "a client requiring StartTLS must fail");
    }

    [Fact]
    public async Task StartTls_MissingCertificate_PlainClient_CanConnectAndSend()
    {
        // Verifies the fallback: the endpoint degrades to plain SMTP when no cert
        // is found, so plain clients (no TLS) can still send mail.
        await using var host = await SmtpTestHost.StartAsync(
            mode: "StartTls",
            withCertificate: false);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage());
        await act.Should().NotThrowAsync(
            "the endpoint falls back to plain SMTP when no certificate is available; " +
            "plain clients must still be able to send");

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task StartTls_MissingCertificate_OptionalTlsClient_ConnectsInPlain()
    {
        // A client using StartTlsWhenAvailable accepts plain connections if
        // STARTTLS is not advertised. This test documents the security trade-off:
        // the client silently sends data unencrypted.
        await using var host = await SmtpTestHost.StartAsync(
            mode: "StartTls",
            withCertificate: false);

        using var client = new SmtpClient();
        // StartTlsWhenAvailable: upgrade to TLS if server supports it, otherwise plain
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.StartTlsWhenAvailable);

        client.IsSecure.Should().BeFalse(
            "no STARTTLS was advertised; the connection is unencrypted");

        var act = () => client.SendAsync(BuildMessage());
        await act.Should().NotThrowAsync();

        await client.DisconnectAsync(quit: true);
    }

    // =========================================================================
    // Implicit TLS mode – missing certificate
    // =========================================================================

    [Fact]
    public async Task ImplicitTls_MissingCertificate_TlsClient_CannotConnect()
    {
        // Server is configured for implicit TLS but no certificate is available.
        // Without ep.IsSecure(true), SmtpServer starts a plain SMTP listener.
        // A TLS client sends a ClientHello immediately; the server responds
        // with a plain-text SMTP greeting → TLS handshake fails.
        await using var host = await SmtpTestHost.StartAsync(
            mode: "Tls",
            withCertificate: false);

        using var client = new SmtpClient();
        client.ServerCertificateValidationCallback = (_, _, _, _) => true;

        var act = () => client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.SslOnConnect);

        await act.Should().ThrowAsync<Exception>(
            "the server has no certificate and cannot complete the implicit TLS handshake");
    }

    [Fact]
    public async Task ImplicitTls_MissingCertificate_PlainClient_CanConnectAndSend()
    {
        // Documents the fallback: the implicit-TLS endpoint degrades to plain.
        await using var host = await SmtpTestHost.StartAsync(
            mode: "Tls",
            withCertificate: false);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        var act = () => client.SendAsync(BuildMessage());
        await act.Should().NotThrowAsync(
            "the endpoint falls back to plain SMTP when no certificate is available");

        await client.DisconnectAsync(quit: true);
    }

    // =========================================================================
    // Fail-closed mode (Certificate.FailClosed = true) – missing certificate
    // =========================================================================

    [Fact]
    public async Task StartTls_MissingCertificate_FailClosed_ListenerDoesNotStart()
    {
        // With Certificate.FailClosed enabled, a TLS listener without a certificate
        // must NOT fall back to plain SMTP — the port stays closed so credentials
        // and mail content can never be transmitted unencrypted.
        await using var host = await SmtpTestHost.StartAsync(
            mode: "StartTls",
            withCertificate: false,
            tlsFailClosed: true,
            waitForListener: false);

        using var client = new SmtpClient();
        client.Timeout = 2000;

        var act = () => client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);

        await act.Should().ThrowAsync<Exception>(
            "the fail-closed listener is not started at all — no plain fallback");
    }

    [Fact]
    public async Task StartTls_WithCertificate_FailClosed_ListenerStartsNormally()
    {
        // Fail-closed only bites when the certificate is MISSING — with a certificate
        // the listener behaves exactly as before.
        await using var host = await SmtpTestHost.StartAsync(
            mode: "StartTls",
            withCertificate: true,
            tlsFailClosed: true);

        using var client = new SmtpClient();
        client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.StartTls);

        client.IsSecure.Should().BeTrue();

        var act = () => client.SendAsync(BuildMessage());
        await act.Should().NotThrowAsync();

        await client.DisconnectAsync(quit: true);
    }

    // =========================================================================
    // Helper
    // =========================================================================

    private static MimeMessage BuildMessage() =>
        new()
        {
            From = { new MailboxAddress("Sender", "sender@example.com") },
            To = { new MailboxAddress("Recipient", "recipient@example.com") },
            Subject = "Certificate Fallback Test",
            Body = new TextPart("plain") { Text = "Test body." },
        };
}
