using System.Diagnostics;
using System.Net.Sockets;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Certificates;
using GraphMailer.Service.Infrastructure.Metrics;
using GraphMailer.Service.Infrastructure.Security;
using GraphMailer.Service.Infrastructure.Smtp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpServer;
using SmtpServer.Authentication;
using SmtpServer.Storage;

namespace GraphMailer.Service.Services;

/// <summary>
/// Manages the lifecycle of one SmtpServer instance per configured endpoint.
///
/// Each entry in the "Servers" config list becomes one listening socket.
/// The service stops all listeners when the host shuts down.
/// </summary>
internal sealed class SmtpRelayService : BackgroundService
{
    private readonly IOptionsMonitor<List<SmtpServerEntry>> _serverEntries;
    private readonly IOptionsMonitor<SmtpOptions> _smtpOptions;
    private readonly IOptionsMonitor<CertificateOptions> _certOptions;
    private readonly ICertificateLoader _certStore;
    private readonly IServiceProvider _serviceProvider;
    private readonly PortProbeRegistry _probeRegistry;
    private readonly IMetricsService _metrics;
    private readonly ILogger<SmtpRelayService> _logger;

    public SmtpRelayService(
        IOptionsMonitor<List<SmtpServerEntry>> serverEntries,
        IOptionsMonitor<SmtpOptions> smtpOptions,
        IOptionsMonitor<CertificateOptions> certOptions,
        ICertificateLoader certStore,
        IServiceProvider serviceProvider,
        PortProbeRegistry probeRegistry,
        IMetricsService metrics,
        ILogger<SmtpRelayService> logger)
    {
        _serverEntries = serverEntries;
        _smtpOptions = smtpOptions;
        _certOptions = certOptions;
        _certStore = certStore;
        _serviceProvider = serviceProvider;
        _probeRegistry = probeRegistry;
        _metrics = metrics;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var allEntries = _serverEntries.CurrentValue;
        if (allEntries.Count == 0)
        {
            _logger.LogWarning("[SmtpRelay] No server entries configured – SMTP relay is inactive.");
            return;
        }

        var entries = SelectActiveListeners(allEntries);
        var disabledCount = allEntries.Count - entries.Count;
        if (disabledCount > 0)
            _logger.LogInformation("[SmtpRelay] {Disabled} listener(s) disabled by configuration – not started.", disabledCount);

        if (entries.Count == 0)
        {
            _logger.LogWarning("[SmtpRelay] All configured listeners are disabled – SMTP relay is inactive.");
            return;
        }

        // Build each listener individually: one broken entry (invalid port, certificate
        // store failure, …) must not prevent the remaining listeners from starting —
        // and must never crash the whole service.
        var startable = SelectStartableListeners(entries, _logger);
        var servers = new List<SmtpServer.SmtpServer>(startable.Count);
        foreach (var entry in startable)
        {
            try
            {
                servers.Add(BuildServer(entry));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[SmtpRelay] Failed to configure listener '{Name}' on port {Port} — this listener is not started.",
                    entry.Name, entry.Port);
            }
        }

        if (servers.Count == 0)
        {
            _logger.LogError(
                "[SmtpRelay] No SMTP listener could be started — the relay is inactive. Fix the Servers section in the configuration.");
            return;
        }

        _logger.LogInformation("[SmtpRelay] Starting {Count} SMTP listener(s)…", servers.Count);

        var tasks = servers.Select(s => s.StartAsync(stoppingToken)).ToArray();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown – not an error
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SmtpRelay] One or more listeners stopped unexpectedly");
        }
        finally
        {
            _logger.LogInformation("[SmtpRelay] All SMTP listeners stopped.");
        }
    }

    // -------------------------------------------------------------------------

    /// <summary>Returns only the listeners that are enabled — disabled entries are never started.</summary>
    internal static List<SmtpServerEntry> SelectActiveListeners(IReadOnlyList<SmtpServerEntry> entries)
        => entries.Where(e => e.Enabled).ToList();

    /// <summary>
    /// Filters out listeners that can never start: out-of-range ports and duplicate
    /// ports (the first entry wins). Each skipped entry is logged as an error — the
    /// log is the operator's only signal that a configured listener is missing.
    /// </summary>
    internal static List<SmtpServerEntry> SelectStartableListeners(
        IReadOnlyList<SmtpServerEntry> entries, ILogger logger)
    {
        var result = new List<SmtpServerEntry>(entries.Count);
        var usedPorts = new HashSet<int>();

        foreach (var entry in entries)
        {
            if (entry.Port is < 1 or > 65535)
            {
                logger.LogError(
                    "[SmtpRelay] Listener '{Name}' has an invalid port {Port} (valid: 1-65535) — this listener is not started.",
                    entry.Name, entry.Port);
                continue;
            }

            if (!usedPorts.Add(entry.Port))
            {
                logger.LogError(
                    "[SmtpRelay] Listener '{Name}' uses port {Port}, which is already assigned to another listener — this listener is not started.",
                    entry.Name, entry.Port);
                continue;
            }

            result.Add(entry);
        }

        return result;
    }

    private SmtpServer.SmtpServer BuildServer(SmtpServerEntry entry)
    {
        var smtpOpts = _smtpOptions.CurrentValue;
        var mode = entry.Mode ?? "Plain";

        var builder = new SmtpServerOptionsBuilder()
            .ServerName(smtpOpts.Banner)
            .MaxMessageSize(EffectiveMaxMessageSize(smtpOpts.MaxSizeBytes, _logger))
            .Endpoint(ep =>
            {
                ep.Port(entry.Port);

                // Plain-mode endpoints must explicitly allow authentication without TLS;
                // otherwise SmtpServer does not advertise AUTH in EHLO and clients
                // cannot authenticate at all. TLS/StartTLS endpoints are secure by
                // default and do not need this flag.
                if (!mode.Equals("Tls", StringComparison.OrdinalIgnoreCase) &&
                    !mode.Equals("StartTls", StringComparison.OrdinalIgnoreCase))
                {
                    ep.AllowUnsecureAuthentication();
                }

                // AuthenticationRequired tells SmtpServer to enforce auth at the
                // protocol level where possible. Our SmtpMailboxFilter additionally
                // enforces it by checking the "Auth:Required" session property.
                if (entry.AuthRequired)
                    ep.AuthenticationRequired(true);

                if (mode.Equals("Tls", StringComparison.OrdinalIgnoreCase) ||
                    mode.Equals("StartTls", StringComparison.OrdinalIgnoreCase))
                {
                    var cert = _certStore.LoadCertificate();
                    if (cert is not null)
                    {
                        ep.Certificate(cert);
                        if (mode.Equals("Tls", StringComparison.OrdinalIgnoreCase))
                            ep.IsSecure(true);
                        _logger.LogInformation(
                            "[SmtpRelay] Port {Port}: TLS certificate loaded for SMTP – Subject={Subject}, Thumbprint={Thumbprint}, Expires={Expires:yyyy-MM-dd}",
                            entry.Port, cert.Subject, cert.Thumbprint, cert.NotAfter);
                    }
                    else if (_certOptions.CurrentValue.FailClosed)
                    {
                        // Fail-closed mode (Certificate.FailClosed = true): refuse to
                        // start this listener rather than expose credentials and mail
                        // content in cleartext. The per-listener catch in ExecuteAsync
                        // logs the error and starts the remaining listeners.
                        throw new InvalidOperationException(
                            $"TLS mode '{mode}' is configured but no certificate was found in the " +
                            "Windows Certificate Store, and Certificate.FailClosed is enabled — " +
                            "this listener is not started as plain SMTP. Install/select a certificate " +
                            "or disable FailClosed to restore the plain fallback.");
                    }
                    else
                    {
                        // SECURITY TRADE-OFF: no certificate found → endpoint starts
                        // as plain SMTP instead of failing.
                        //
                        // Consequences:
                        //  • Implicit TLS (mode=Tls):   TLS clients fail to connect
                        //    (TLS handshake against a plain-text server).
                        //  • StartTLS (mode=StartTls):  STARTTLS is not advertised in
                        //    EHLO. Clients requiring STARTTLS cannot connect; clients
                        //    using StartTlsWhenAvailable silently send data in plaintext.
                        //
                        // This design keeps the service operational when a cert is
                        // temporarily missing (e.g. after rotation). The Error log
                        // below ensures the operator is alerted immediately.
                        // Configure Certificate.SubjectName in graphmailer.json to fix.
                        _logger.LogError(
                            "[SmtpRelay] Port {Port}: TLS mode '{Mode}' is configured but " +
                            "no certificate was found in the Windows Certificate Store. " +
                            "The endpoint has started as plain SMTP – credentials and mail " +
                            "content are transmitted unencrypted. " +
                            "Set Certificate.SubjectName in config/graphmailer.json.",
                            entry.Port, mode);
                    }
                }
            });

        var options = builder.Build();

        _logger.LogInformation(
            "[SmtpRelay] Configured listener on port {Port} (mode: {Mode}, auth: {Auth})",
            entry.Port, mode, entry.AuthRequired ? "required" : "optional");

        var server = new SmtpServer.SmtpServer(options, _serviceProvider);
        AttachSessionLogging(server, entry);
        return server;
    }

    /// <summary>
    /// The SmtpServer library takes the max message size as an int. A configured value
    /// above int.MaxValue (~2 GB) is clamped — with a warning, so the operator knows the
    /// advertised EHLO SIZE differs from the configured value instead of a silent cap.
    /// </summary>
    internal static int EffectiveMaxMessageSize(long configuredBytes, ILogger logger)
    {
        if (configuredBytes <= int.MaxValue)
            return (int)configuredBytes;

        logger.LogWarning(
            "[SmtpRelay] Smtp.MaxSizeBytes ({Configured:N0} bytes) exceeds the supported maximum of {Max:N0} bytes — the advertised SIZE is clamped.",
            configuredBytes, int.MaxValue);
        return int.MaxValue;
    }

    // -------------------------------------------------------------------------
    // Session tracking: per-session progress for the statistics store and the
    // end-of-session summary log line (identifies clients that connect and
    // disconnect without QUIT, e.g. monitoring probes).
    // -------------------------------------------------------------------------

    private const string TrackerKey = "Metrics:SessionTracker";
    private const int MaxLoggedCommands = 20;

    private sealed class SessionTracker
    {
        public readonly Stopwatch Stopwatch = Stopwatch.StartNew();
        public string LastStage = SessionStages.Connect;
        public bool QuitSeen;
        public bool Finalized;
        public int TotalCommands;
        public readonly List<string> Commands = [];
    }

    /// <summary>Advances the tracker after a command has executed. RSET/NOOP/PROXY are
    /// listed in the command trace but never advance the stage — "aborted after AUTH"
    /// must survive a trailing NOOP.</summary>
    private static void OnCommandExecuted(SessionTracker tracker, SmtpCommandEventArgs e)
    {
        var name = e.Command?.Name?.ToUpperInvariant() ?? "?";

        tracker.TotalCommands++;
        if (tracker.Commands.Count < MaxLoggedCommands)
            tracker.Commands.Add(name);

        var stage = name switch
        {
            "HELO" => SessionStages.Helo,
            "EHLO" => SessionStages.Ehlo,
            "STARTTLS" => SessionStages.StartTls,
            "AUTH" => SessionStages.Auth,
            "MAIL" => SessionStages.Mail,
            "RCPT" => SessionStages.Rcpt,
            "DATA" => SessionStages.Data,
            "QUIT" => SessionStages.Quit,
            _ => null,
        };
        if (stage is not null)
            tracker.LastStage = stage;
        if (name == "QUIT")
            tracker.QuitSeen = true;
    }

    /// <summary>
    /// Records the finished session into the hourly statistics bucket and optionally logs
    /// the one-line summary. Idempotent per session: SmtpServer can raise Faulted and
    /// Completed for the same session — only the first call counts.
    /// </summary>
    private void FinalizeSession(ISessionContext context, SmtpServerEntry entry, string ip,
        SessionOutcome outcome, bool logSummary)
    {
        if (!context.Properties.TryGetValue(TrackerKey, out var value) || value is not SessionTracker tracker)
            return;   // probe connection or tracking not attached
        if (tracker.Finalized)
            return;
        tracker.Finalized = true;
        tracker.Stopwatch.Stop();

        // A "completed" session that never said QUIT was dropped by the client.
        if (outcome == SessionOutcome.Clean && !tracker.QuitSeen)
            outcome = SessionOutcome.Aborted;

        // The pipe/auth context may already be torn down on faulted sessions.
        var tls = false;
        var authenticated = false;
        string authUser = "";
        try { tls = context.Pipe?.IsSecure ?? false; } catch { /* disposed */ }
        try
        {
            authenticated = context.Authentication?.IsAuthenticated ?? false;
            authUser = context.Authentication?.User ?? "";
        }
        catch { /* disposed */ }

        if (logSummary)
        {
            var commands = tracker.TotalCommands > MaxLoggedCommands
                ? $"{string.Join(",", tracker.Commands)},+{tracker.TotalCommands - MaxLoggedCommands} more"
                : tracker.Commands.Count > 0 ? string.Join(",", tracker.Commands) : "(none)";
            _logger.LogInformation(
                "[SmtpRelay] Session ended for {Ip} on port {Port}: outcome={Outcome}, last stage={Stage}, tls={Tls}, auth={AuthUser}, commands={Commands}, duration={DurationMs}ms",
                ip, entry.Port,
                outcome.ToString().ToLowerInvariant(), tracker.LastStage,
                tls ? "yes" : "no",
                authenticated ? authUser : "no",
                commands, tracker.Stopwatch.ElapsedMilliseconds);
        }

        // Fire-and-forget: metrics must never block or fault the SMTP event pipeline
        // (RecordSmtpSessionAsync catches and logs all its own errors).
        _ = _metrics.RecordSmtpSessionAsync(new SmtpSessionRecord
        {
            ClientIp = ip,
            ListenerPort = entry.Port,
            Outcome = outcome,
            LastStage = tracker.LastStage,
            Tls = tls,
            Authenticated = authenticated,
            DurationMs = tracker.Stopwatch.ElapsedMilliseconds,
        });
    }

    private void AttachSessionLogging(SmtpServer.SmtpServer server, SmtpServerEntry entry)
    {
        server.SessionCreated += (_, e) =>
        {
            var ip = IpFilterService.GetRemoteIp(e.Context)?.ToString() ?? "unknown";

            // Local health-check probes (PortMonitor) announce themselves —
            // keep them out of the Information log and out of the statistics.
            if (_probeRegistry.IsProbeConnection(entry.Port, ip))
            {
                _logger.LogDebug("[SmtpRelay] Health-probe connection from {Ip} on port {Port}", ip, entry.Port);
            }
            else
            {
                _logger.LogInformation("[SmtpRelay] Connection accepted from {Ip} on port {Port}", ip, entry.Port);

                var tracker = new SessionTracker();
                e.Context.Properties[TrackerKey] = tracker;
                e.Context.CommandExecuted += (_, ce) => OnCommandExecuted(tracker, ce);
            }

            // Tag sessions on auth-required endpoints so SmtpMailboxFilter can
            // reject unauthenticated MAIL FROM even when SmtpServer itself does not.
            if (entry.AuthRequired)
                e.Context.Properties["Auth:Required"] = true;
        };

        server.SessionCompleted += (_, e) =>
        {
            var ip = IpFilterService.GetRemoteIp(e.Context)?.ToString() ?? "unknown";
            if (_probeRegistry.IsProbeConnection(entry.Port, ip))
                _logger.LogDebug("[SmtpRelay] Health-probe session ended for {Ip} on port {Port}", ip, entry.Port);
            else
                FinalizeSession(e.Context, entry, ip, SessionOutcome.Clean, logSummary: true);
        };

        server.SessionFaulted += (_, e) =>
        {
            var ip = IpFilterService.GetRemoteIp(e.Context)?.ToString() ?? "unknown";

            // A health probe connects and drops without TLS/SMTP — the resulting
            // fault (failed handshake, EOF, reset) is expected, not a client error.
            if (_probeRegistry.IsProbeConnection(entry.Port, ip))
            {
                _logger.LogDebug(
                    "[SmtpRelay] Health-probe session faulted (expected) for {Ip} on port {Port}: {Reason}",
                    ip, entry.Port, e.Exception?.Message ?? "unknown");
                return;
            }

            FinalizeSession(e.Context, entry, ip, SessionOutcome.Faulted, logSummary: false);

            // IOException "unexpected EOF / 0 bytes" happens when a plain-text client
            // connects to an Implicit-TLS port (port 465) or the client drops the
            // connection before the TLS handshake completes. This is a normal client
            // error, not a service defect — log the reason at Info without stack trace.
            if (e.Exception is System.IO.IOException ioEx &&
                (ioEx.Message.Contains("EOF", StringComparison.OrdinalIgnoreCase) ||
                 ioEx.Message.Contains("0 bytes", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogInformation(
                    "[SmtpRelay] Session faulted for {Ip} on port {Port}: TLS handshake incomplete " +
                    "(client disconnected or connected without TLS to an Implicit-TLS port).",
                    ip, entry.Port);
                return;
            }

            // SocketException 10053/10054 (ConnectionAborted/ConnectionReset) wrapped in
            // IOException: the remote host closed the TCP connection before sending any
            // SMTP commands. This is the normal outcome of a TCP health-check probe
            // (e.g. PortMonitor) or a scanning tool that connects and immediately drops.
            // Log at Debug to suppress the per-check noise; the stack trace adds nothing.
            if (e.Exception is System.IO.IOException { InnerException: SocketException sockEx } &&
                sockEx.SocketErrorCode is SocketError.ConnectionAborted or SocketError.ConnectionReset)
            {
                _logger.LogDebug(
                    "[SmtpRelay] Session closed by remote host for {Ip} on port {Port} ({Error})",
                    ip, entry.Port, sockEx.SocketErrorCode);
                return;
            }

            _logger.LogWarning(e.Exception,
                "[SmtpRelay] Session faulted for {Ip} on port {Port}: {Reason}",
                ip, entry.Port, e.Exception?.Message ?? "unknown error");
        };

        server.SessionCancelled += (_, e) =>
        {
            var ip = IpFilterService.GetRemoteIp(e.Context)?.ToString() ?? "unknown";
            _logger.LogDebug("[SmtpRelay] Session cancelled for {Ip} on port {Port}", ip, entry.Port);
            if (!_probeRegistry.IsProbeConnection(entry.Port, ip))
                FinalizeSession(e.Context, entry, ip, SessionOutcome.Cancelled, logSummary: false);
        };
    }
}
