using GraphMailer.Service;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure;
using GraphMailer.Service.Infrastructure.Backup;
using GraphMailer.Service.Infrastructure.Certificates;
using GraphMailer.Service.Infrastructure.Encryption;
using GraphMailer.Service.Infrastructure.Metrics;
using GraphMailer.Service.Infrastructure.Security;
using GraphMailer.Service.Infrastructure.Smtp;
using GraphMailer.Service.Services;
using GraphMailer.Service.Infrastructure.Validation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Serilog;
using SmtpServer.Authentication;
using SmtpServer.Storage;

// Handle CLI arguments before building the host
if (args.Length > 0)
{
    return args[0].ToLowerInvariant() switch
    {
        "--install" => ServiceManager.Install(),
        "--uninstall" => ServiceManager.Uninstall(),
        "--status" => ServiceManager.Status(),
        _ => 0
    };
}

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    // Machine-wide single-instance lock: a second GraphMailer.exe (e.g. started
    // manually while the Windows service is running) must not compete for the
    // SMTP ports and the mail queue. CLI commands above are exempt.
    using var singleInstance = new SingleInstanceGuard("GraphMailer.Service");
    if (!singleInstance.IsPrimaryInstance)
    {
        Log.Fatal("[GraphMailer] Another instance of the GraphMailer service is already running – exiting");
        return 1;
    }

    Log.Information("[GraphMailer] Starting up... version {Version} (runtime: {Runtime})",
        BuildInfo.FileVersion, System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);

    var builder = Host.CreateApplicationBuilder(args);

    // -----------------------------------------------------------------------
    // Configuration sources (in order – later sources override earlier ones):
    //   1. C# property initialisers  (hardcoded defaults)
    //   2. appsettings.json          (bundled defaults, do not edit)
    //   3. appsettings.{env}.json    (environment-specific overrides)
    //   4. config/graphmailer.json   (user-writable overrides, ENC[...] values decrypted)
    //   5. Environment variables     (prefix: GRAPHMAILER_, __ = section separator)
    //   6. Command-line arguments
    // -----------------------------------------------------------------------

    // Build a protector early – config must be decrypted before full DI is ready.
    // Uses the same Registry key ring as the protector registered below in DI.
    var configProtector = DataProtectionExtensions.BuildConfigProtector();

    var userConfigPath = AppPaths.ConfigFilePath;

    // A syntactically corrupt user config (truncated hand-edit) would otherwise throw
    // inside host.Build() on EVERY start — quarantine it and start on built-in defaults
    // (first-run provisioning below re-seeds the listeners) instead of staying down.
    var quarantined = GraphMailer.Service.Infrastructure.Config.ConfigMigrator.QuarantineIfCorrupt(userConfigPath);
    if (quarantined is not null)
        Log.Error("[GraphMailer] graphmailer.json contains invalid JSON and was quarantined to {Path} — " +
                  "starting with built-in defaults. Repair the quarantined file and rename it back to restore the configuration.",
            quarantined);

    // Migrate the config schema up to the version this build understands BEFORE it is read.
    // Backs up the original; no-op when already current. Newer-than-build is used as-is.
    var configMigration = GraphMailer.Service.Infrastructure.Config.ConfigMigrator.MigrateFile(userConfigPath);
    if (configMigration.Incompatible)
        Log.Error("[GraphMailer] graphmailer.json schema v{Found} is newer than this build (v{Supported}) — used as-is; upgrade the service",
            configMigration.From, GraphMailer.Service.Infrastructure.Config.ConfigSchema.Current);
    else if (configMigration.Migrated)
        Log.Information("[GraphMailer] Migrated graphmailer.json schema v{From} → v{To} (backup: {Backup})",
            configMigration.From, configMigration.To, configMigration.BackupPath);

    // First run (no graphmailer.json yet): seed the default listeners + IP whitelist and
    // generate/bind a self-signed TLS certificate, so the service starts with a sensible,
    // working configuration. No-op once the file exists. Must run before the config is loaded.
    GraphMailer.Service.Infrastructure.Config.FirstRunProvisioner.EnsureProvisioned(userConfigPath, configProtector);

    if (File.Exists(userConfigPath))
        Log.Information("[GraphMailer] Loading user config from {Path}", userConfigPath);
    else
        Log.Debug("[GraphMailer] No user config found at {Path} – using built-in defaults", userConfigPath);
    builder.Configuration.AddEncryptedJsonFile(userConfigPath, configProtector, optional: true, reloadOnChange: true);
    builder.Configuration.AddEnvironmentVariables(prefix: "GRAPHMAILER_");

    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "GraphMailer";
    });

    // A single faulted background service (e.g. a monitoring worker) must not stop
    // the whole host — the default StopHost behaviour would take SMTP relaying and
    // queue processing down with it. Faults are still logged by the hosting
    // infrastructure; SmtpRelayService and QueueProcessor additionally guard their
    // own loops. The shutdown timeout is explicit so a queue batch mid-drain has a
    // predictable stop window under the Windows SCM.
    builder.Services.Configure<HostOptions>(options =>
    {
        options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
        options.ShutdownTimeout = TimeSpan.FromSeconds(30);
    });

    builder.Services.AddSerilog((services, lc) => lc
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        // rollOnFileSizeLimit: a day exceeding the size limit rolls to a _001 file
        // instead of silently dropping the rest of that day's log output.
        .WriteTo.File(
            Path.Combine(AppPaths.LogsDir, "graphmailer-.log"),
            rollingInterval: Serilog.RollingInterval.Day,
            retainedFileCountLimit: 7,
            fileSizeLimitBytes: 104_857_600,
            rollOnFileSizeLimit: true)
        .WriteTo.File(
            Path.Combine(AppPaths.LogsDir, "error-.log"),
            rollingInterval: Serilog.RollingInterval.Day,
            retainedFileCountLimit: 30,
            fileSizeLimitBytes: 104_857_600,
            rollOnFileSizeLimit: true,
            restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Error));

    // Register all strongly-typed options sections
    builder.Services.Configure<CertificateOptions>(builder.Configuration.GetSection(CertificateOptions.SectionName));
    builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));
    builder.Services.Configure<GraphApiOptions>(builder.Configuration.GetSection(GraphApiOptions.SectionName));
    builder.Services.Configure<MailQueueOptions>(builder.Configuration.GetSection(MailQueueOptions.SectionName));
    builder.Services.Configure<IpBlockingProtectionOptions>(builder.Configuration.GetSection(IpBlockingProtectionOptions.SectionName));
    builder.Services.Configure<CertificateMonitoringOptions>(builder.Configuration.GetSection(CertificateMonitoringOptions.SectionName));
    builder.Services.Configure<DiskSpaceMonitoringOptions>(builder.Configuration.GetSection(DiskSpaceMonitoringOptions.SectionName));
    builder.Services.Configure<PortMonitoringOptions>(builder.Configuration.GetSection(PortMonitoringOptions.SectionName));
    builder.Services.Configure<GraphApiMonitoringOptions>(builder.Configuration.GetSection(GraphApiMonitoringOptions.SectionName));
    builder.Services.Configure<UpdateCheckOptions>(builder.Configuration.GetSection(UpdateCheckOptions.SectionName));
    builder.Services.Configure<TelemetryOptions>(builder.Configuration.GetSection(TelemetryOptions.SectionName));
    builder.Services.Configure<MetricsOptions>(builder.Configuration.GetSection(MetricsOptions.SectionName));
    builder.Services.Configure<AdminNotificationsOptions>(builder.Configuration.GetSection(AdminNotificationsOptions.SectionName));
    builder.Services.Configure<NdrOptions>(builder.Configuration.GetSection(NdrOptions.SectionName));
    builder.Services.Configure<SenderValidationOptions>(builder.Configuration.GetSection(SenderValidationOptions.SectionName));
    builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection(BackupOptions.SectionName));
    builder.Services.Configure<RecommendationOptions>(builder.Configuration.GetSection(RecommendationOptions.SectionName));

    // Servers list (not a single-section option – bound directly as IList)
    builder.Services.Configure<List<SmtpServerEntry>>(builder.Configuration.GetSection("Servers"));

    // Access-control lists are each a separate top-level JSON array;
    // bind the whole config root so IOptionsMonitor reloads on file changes.
    builder.Services.Configure<SmtpAccessOptions>(builder.Configuration);

    // Validate critical options at startup
    builder.Services.AddSingleton<IValidateOptions<GraphApiOptions>, GraphApiOptionsValidator>();
    builder.Services.AddSingleton<IValidateOptions<SmtpOptions>, SmtpOptionsValidator>();

    // Data Protection (for dashboard encrypt/decrypt in Phase 5, same key ring as above)
    builder.Services.AddDataProtection()
        .SetApplicationName(DataProtectionExtensions.ApplicationName)
        .PersistToRegistryOrFallback();

    // Phase 2 – Security infrastructure
    builder.Services.AddSingleton<IpBlockingService>();
    builder.Services.AddSingleton<AuthHandler>();
    builder.Services.AddSingleton<IPasswordCaptureService, PasswordCaptureService>();
    builder.Services.AddSingleton<ICertificateLoader, CertificateStoreService>();
    builder.Services.AddSingleton<CertificateStoreService>();

    // Phase 2 – SmtpServer handler registrations (resolved by SmtpServer’s IServiceProvider)
    builder.Services.AddSingleton<IUserAuthenticator, SmtpUserAuthenticator>();
    builder.Services.AddSingleton<IMailboxFilter, SmtpMailboxFilter>();
    builder.Services.AddSingleton<IMessageStore, SmtpMessageStore>();

    // Phase 2 – Queue writer + SMTP relay service
    builder.Services.AddSingleton<MailQueueWriter>();
    builder.Services.AddSingleton<PortProbeRegistry>();
    builder.Services.AddHostedService<SmtpRelayService>();

    // Phase 3 – Graph API delivery
    builder.Services.AddSingleton<GraphClientProvider>();
    builder.Services.AddSingleton<IGraphApiClient, GraphApiClient>();
    builder.Services.AddHostedService<QueueProcessor>();

    // Phase 3 – Tenant sender validation (MAIL FROM against tenant directory)
    builder.Services.AddSingleton<IGraphDirectoryGateway, GraphDirectoryGateway>();
    builder.Services.AddSingleton<ITenantSenderDirectory, TenantSenderDirectory>();
    builder.Services.AddHostedService<SenderDirectorySyncService>();

    // Phase 4 – Metrics (concrete type also registered for MetricsCollectorService)
    builder.Services.AddSingleton<MetricsService>();
    builder.Services.AddSingleton<IMetricsService>(sp => sp.GetRequiredService<MetricsService>());
    builder.Services.AddHostedService<MetricsCollectorService>();

    // Phase 4 – Admin notifications
    builder.Services.AddSingleton<IAdminNotificationService, AdminNotificationService>();

    // Periodic operations report (weekly/monthly HTML email to admin recipients)
    builder.Services.AddSingleton<GraphMailer.Service.Services.Reporting.ReportDataCollector>();
    builder.Services.AddHostedService<GraphMailer.Service.Services.Reporting.ScheduledReportService>();

    // Startup integrity check: verify all ENC[...] config secrets decrypt with the
    // current Data Protection key ring (catches a key-ring/config mismatch eagerly
    // instead of failing silently at the first Graph call or SMTP login).
    builder.Services.AddHostedService<SecretIntegrityCheckService>();

    // Configuration backups: shared builder/restorer + scheduled background service.
    builder.Services.AddSingleton<IConfigBackupService>(sp =>
        new ConfigBackupService(
            sp.GetRequiredService<IDataProtectionProvider>()
              .CreateProtector(DataProtectionExtensions.ConfigPurpose)));
    builder.Services.AddHostedService<BackupBackgroundService>();

    // Phase 4 – Monitoring services
    builder.Services.AddSingleton<IGraphConnectivityProbe, GraphConnectivityProbe>();
    builder.Services.AddHostedService<CertificateMonitoringService>();
    builder.Services.AddHostedService<DiskSpaceMonitoringService>();
    builder.Services.AddHostedService<PortMonitoringService>();
    builder.Services.AddHostedService<GraphApiMonitoringService>();

    // Opt-in weekly GitHub release check (status file for the ConfigTool + optional admin mail)
    builder.Services.AddSingleton<GraphMailer.Service.Services.UpdateCheck.IUpdateChecker,
        GraphMailer.Service.Services.UpdateCheck.GitHubUpdateChecker>();
    builder.Services.AddHostedService<GraphMailer.Service.Services.UpdateCheck.UpdateCheckService>();

    // Opt-in anonymous telemetry: daily heartbeat + PII-free error reports to the
    // developer's Application Insights. The sink is picked up by Serilog via
    // ReadFrom.Services(...) above; it only forwards while Telemetry.Enabled is true.
    builder.Services.AddSingleton<GraphMailer.Service.Services.Telemetry.ErrorReportCollector>();
    builder.Services.AddSingleton<Serilog.Core.ILogEventSink, GraphMailer.Service.Services.Telemetry.TelemetrySink>();
    builder.Services.AddSingleton<GraphMailer.Service.Services.Telemetry.ITelemetrySender,
        GraphMailer.Service.Services.Telemetry.AppInsightsTelemetrySender>();
    builder.Services.AddHostedService<GraphMailer.Service.Services.Telemetry.TelemetryService>();

    builder.Services.AddHostedService<Worker>();

    var host = builder.Build();

    // Logged after Build() so it reaches the file sinks (the bootstrap line above is
    // console-only). Version is derived from the assembly — no manual upkeep.
    Log.Information("[GraphMailer] GraphMailer service {Version} ({InformationalVersion}) starting on {Machine}",
        BuildInfo.FileVersion, BuildInfo.InformationalVersion, Environment.MachineName);

    host.Run();

    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "[GraphMailer] Terminated unexpectedly");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}
