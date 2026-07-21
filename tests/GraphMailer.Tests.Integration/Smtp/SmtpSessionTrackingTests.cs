using System.Collections.Concurrent;
using GraphMailer.Service.Infrastructure.Metrics;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using NSubstitute;

namespace GraphMailer.Tests.Integration.Smtp;

/// <summary>
/// Verifies the SMTP session statistics introduced with metrics.db schema v2:
/// session outcome/stage aggregation (incl. clients that disconnect without QUIT —
/// the "monitoring probe" pattern), rejection counters, the reception context on
/// received-mail events, and the end-of-session summary log line.
/// </summary>
[Collection("SmtpIntegration")]
public class SmtpSessionTrackingTests
{
    // -------------------------------------------------------------------------
    // Session outcomes
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Session_ClientQuits_RecordsCleanOutcomeWithQuitStage()
    {
        await using var host = await SmtpTestHost.StartAsync();
        var sessions = CaptureSessions(host);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);
        await client.SendAsync(BuildMessage());
        await client.DisconnectAsync(quit: true);

        var session = await WaitForSessionAsync(sessions, s => s.Outcome == SessionOutcome.Clean);
        session.LastStage.Should().Be(SessionStages.Quit);
        session.ListenerPort.Should().Be(host.Port);
        session.ClientIp.Should().Be("127.0.0.1");
        session.Tls.Should().BeFalse();
    }

    [Fact]
    public async Task Session_ClientDropsAfterAuth_RecordsAbortedWithAuthStage()
    {
        // The customer-observed monitoring pattern: connect → EHLO → AUTH → hard drop.
        // In the plain log this is nearly invisible; the session statistics must make
        // it countable (outcome=aborted, last stage=auth, authenticated).
        await using var host = await SmtpTestHost.StartAsync(users: [("monitor", "check123")]);
        var sessions = CaptureSessions(host);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);
        await client.AuthenticateAsync("monitor", "check123");
        await client.DisconnectAsync(quit: false);   // drop without QUIT

        var session = await WaitForSessionAsync(sessions,
            s => s.Outcome == SessionOutcome.Aborted && s.LastStage == SessionStages.Auth);
        session.Authenticated.Should().BeTrue();
        session.ListenerPort.Should().Be(host.Port);
    }

    [Fact]
    public async Task Session_OverStartTls_RecordsTlsFlag()
    {
        await using var host = await SmtpTestHost.StartAsync(mode: "StartTls");
        var sessions = CaptureSessions(host);

        using var client = new SmtpClient();
        client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.StartTls);
        await client.SendAsync(BuildMessage());
        await client.DisconnectAsync(quit: true);

        var session = await WaitForSessionAsync(sessions, s => s.Outcome == SessionOutcome.Clean);
        session.Tls.Should().BeTrue("the session upgraded to TLS via STARTTLS");
    }

    // -------------------------------------------------------------------------
    // Rejection counters
    // -------------------------------------------------------------------------

    [Fact]
    public async Task BlacklistedIp_RecordsIpBlacklistRejection()
    {
        await using var host = await SmtpTestHost.StartAsync(ipBlacklist: ["127.0.0.1"]);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);
        var act = () => client.SendAsync(BuildMessage());
        await act.Should().ThrowAsync<SmtpCommandException>();

        await host.Metrics.Received(1).RecordRejectionAsync(
            RejectionReasons.IpBlacklist, "127.0.0.1", host.Port, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotWhitelistedIp_RecordsIpNotWhitelistedRejection()
    {
        await using var host = await SmtpTestHost.StartAsync(ipWhitelist: ["10.99.0.0/16"]);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);
        var act = () => client.SendAsync(BuildMessage());
        await act.Should().ThrowAsync<SmtpCommandException>();

        await host.Metrics.Received(1).RecordRejectionAsync(
            RejectionReasons.IpNotWhitelisted, "127.0.0.1", host.Port, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FailedAuth_RecordsAuthFailedRejection()
    {
        await using var host = await SmtpTestHost.StartAsync(users: [("testuser", "testpass")]);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);
        await Assert.ThrowsAnyAsync<Exception>(
            () => client.AuthenticateAsync("testuser", "wrongpass"));

        await host.Metrics.Received().RecordRejectionAsync(
            RejectionReasons.AuthFailed, "127.0.0.1", host.Port, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BlockedRecipient_RecordsBlockedRecipientRejection()
    {
        await using var host = await SmtpTestHost.StartAsync(blockedRecipients: ["recipient@example.com"]);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);
        var act = () => client.SendAsync(BuildMessage(to: "recipient@example.com"));
        await act.Should().ThrowAsync<SmtpCommandException>();

        await host.Metrics.Received(1).RecordRejectionAsync(
            RejectionReasons.BlockedRecipient, "127.0.0.1", host.Port, Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // Reception context on received-mail events
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ReceivedMail_RecordsListenerAuthAndTlsContext()
    {
        await using var host = await SmtpTestHost.StartAsync(users: [("relayuser", "secret1")]);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);
        await client.AuthenticateAsync("relayuser", "secret1");
        await client.SendAsync(BuildMessage());
        await client.DisconnectAsync(quit: true);

        await host.Metrics.Received(1).RecordEmailReceivedAsync(
            Arg.Is<ReceivedEmailEvent>(e =>
                e.ListenerPort == host.Port &&
                e.ClientIp == "127.0.0.1" &&
                e.Authenticated &&
                e.AuthUser == "relayuser" &&
                !e.Tls),
            Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // Session summary log line
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AbortedSession_LogsSummaryWithOutcomeAndStage()
    {
        // The summary line is the operator's only in-log signal that a client
        // disconnected without QUIT (monitoring probes) — level + key facts only.
        var logs = new CapturingLoggerProvider();
        await using var host = await SmtpTestHost.StartAsync(
            users: [("monitor", "check123")], loggerProvider: logs);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);
        await client.AuthenticateAsync("monitor", "check123");
        await client.DisconnectAsync(quit: false);

        await WaitUntilAsync(() => logs.Entries.Any(e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("Session ended") &&
            e.Message.Contains("outcome=aborted") &&
            e.Message.Contains("last stage=auth")));
    }

    [Fact]
    public async Task Session_LogsSummaryWithClientAnnouncedHeloName()
    {
        // The HELO/EHLO name is often the only way to tell apart several clients
        // behind the same source IP (NAT, load balancer, app server).
        var logs = new CapturingLoggerProvider();
        await using var host = await SmtpTestHost.StartAsync(loggerProvider: logs);

        using var client = new SmtpClient { LocalDomain = "legacy-app.example.local" };
        await client.ConnectAsync("127.0.0.1", host.Port, SecureSocketOptions.None);
        await client.SendAsync(BuildMessage());
        await client.DisconnectAsync(quit: true);

        await WaitUntilAsync(() => logs.Entries.Any(e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("Session ended") &&
            e.Message.Contains("helo=legacy-app.example.local")));
    }

    [Fact]
    public async Task Session_ClientNeverGreets_LogsSummaryWithHeloNone()
    {
        // A client that quits without ever greeting — the summary must stay readable
        // instead of showing an empty helo= field.
        var logs = new CapturingLoggerProvider();
        await using var host = await SmtpTestHost.StartAsync(loggerProvider: logs);

        using (var tcp = new System.Net.Sockets.TcpClient())
        {
            await tcp.ConnectAsync("127.0.0.1", host.Port);
            await using var stream = tcp.GetStream();
            using var reader = new StreamReader(stream);
            await reader.ReadLineAsync();                       // 220 banner
            var quit = "QUIT\r\n"u8.ToArray();
            await stream.WriteAsync(quit);
            await reader.ReadLineAsync();                       // 221 bye
        }

        await WaitUntilAsync(() => logs.Entries.Any(e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("Session ended") &&
            e.Message.Contains("helo=(none)")));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static MimeMessage BuildMessage(string from = "sender@example.com", string to = "rcpt@example.com")
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = "Session tracking test";
        message.Body = new TextPart("plain") { Text = "Hello" };
        return message;
    }

    /// <summary>
    /// Collects every SmtpSessionRecord the host reports. The host's startup
    /// readiness probe (TcpClient connect + drop) also produces a session — tests
    /// must filter by outcome/stage instead of asserting counts.
    /// </summary>
    private static ConcurrentQueue<SmtpSessionRecord> CaptureSessions(SmtpTestHost host)
    {
        var sessions = new ConcurrentQueue<SmtpSessionRecord>();
        host.Metrics
            .When(m => m.RecordSmtpSessionAsync(Arg.Any<SmtpSessionRecord>(), Arg.Any<CancellationToken>()))
            .Do(ci => sessions.Enqueue(ci.Arg<SmtpSessionRecord>()));
        return sessions;
    }

    /// <summary>Session records are written when the server observes the disconnect — poll briefly.</summary>
    private static async Task<SmtpSessionRecord> WaitForSessionAsync(
        ConcurrentQueue<SmtpSessionRecord> sessions,
        Func<SmtpSessionRecord, bool> match,
        int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var found = sessions.FirstOrDefault(match);
            if (found is not null) return found;
            await Task.Delay(50);
        }
        throw new TimeoutException(
            $"Expected session record did not arrive within {timeoutMs} ms. " +
            $"Captured: [{string.Join("; ", sessions.Select(s => $"{s.Outcome}/{s.LastStage}"))}]");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        condition().Should().BeTrue($"expected condition within {timeoutMs} ms");
    }

    /// <summary>Captures all host log output for asserting the session summary line.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public record Entry(LogLevel Level, string Message);

        private readonly ConcurrentQueue<Entry> _entries = new();

        public IReadOnlyCollection<Entry> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries);

        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<Entry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue(new Entry(logLevel, formatter(state, exception)));
        }
    }
}
