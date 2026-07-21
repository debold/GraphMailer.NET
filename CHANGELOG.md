# Changelog

## Unreleased

### Changed

- **GraphMailer now runs on .NET 10 instead of .NET 8.** .NET 8 support ends in November 2026;
  .NET 10 is the current LTS and is supported into November 2028. Nothing about how GraphMailer
  behaves changes — this is a platform move.

  > **Upgrading an existing installation:** the target machine now needs the **.NET 10 Desktop
  > Runtime (x64)**. Use `GraphMailerSetup-<version>.exe`, which installs it for you. The bare
  > `GraphMailer-<version>.msi` now **refuses to install** when that runtime is missing, instead
  > of leaving behind a registered service that cannot start. Your configuration, secrets, queue
  > and metrics in `C:\ProgramData\GraphMailer\` are untouched by the upgrade.

- **Certificate loading moved to the current .NET API.** Self-signed SMTP certificate creation
  and the Entra setup wizard now use `X509CertificateLoader`; the constructors they used before
  are obsolete as of .NET 9. No change in behaviour or in the certificates produced.
- **All framework dependencies moved onto the .NET 10 line** alongside the runtime:
  `Microsoft.AspNetCore.DataProtection`, `Microsoft.Extensions.Configuration.*`,
  `Microsoft.Extensions.Hosting.WindowsServices` and `Microsoft.Data.Sqlite` to 10.0.10,
  `Serilog.Extensions.Hosting` to 10.0.0 and `Serilog.Settings.Configuration` to 10.0.1. No
  8.0.x package remains. Behaviour is unchanged throughout: the Data Protection key ring format
  still reads existing `ENC[...]` secrets, and `metrics.db` keeps its format and migrations.

## 1.2.4.1031 — 2026-07-21

Reworked list and details layout across Logs, Messages and the new Activity tab, plus a full
dependency maintenance round: every third-party component was reviewed against
`internal/DEPENDENCY_UPDATE_PLAYBOOK.md`, and all eleven security advisories that affected the
project are closed — service and ConfigTool both report no vulnerable packages at all. The
dependency work is not intended to change any behaviour.

### Security

- **Updated `Microsoft.AspNetCore.DataProtection` 8.0.0 → 8.0.29**, which pulls in
  `System.Security.Cryptography.Xml` 8.0.4 and closes seven High-severity advisories in that
  transitive dependency (GHSA-mmjf-rqrv-855v, GHSA-37gx-xxp4-5rgx, GHSA-w3x6-4m5h-cxqf,
  GHSA-g8r8-53c2-pm3f, GHSA-8q5v-6pqq-x66h, GHSA-cvvh-rhrc-wg4q, GHSA-23rf-6693-g89p).
  Data Protection encrypts every `ENC[...]` secret in `graphmailer.json`; the key ring format
  is unchanged, so existing secrets keep decrypting.
- **Pinned `SQLitePCLRaw.bundle_e_sqlite3` to 2.1.12**, closing CVE-2025-6965 /
  GHSA-2m69-gcr7-jv3q (High, memory corruption in the bundled SQLite). `Microsoft.Data.Sqlite`
  8.0.29 — the highest version available for .NET 8 — still resolves SQLitePCLRaw 2.1.6, so no
  parent update fixes this and an explicit pin was required. `metrics.db` now runs on SQLite
  3.53.3; the file format is unchanged.
- **Closed CVE-2026-44503 / GHSA-7j59-v9qr-6fq9** (High) in the Kiota libraries underneath the
  Graph SDK: their redirect handler stripped the `Authorization` header when following a 3xx to
  a different host, but forwarded `Cookie`, `Proxy-Authorization` and all custom headers. The
  Graph SDK 6 upgrade below pulls Kiota 2.0.0, well past the fixed 1.22.0.
- **Updated MailKit 4.11.0 → 4.17.0 in the integration test suite**, closing
  GHSA-9j88-vvj5-vhgr (Moderate, STARTTLS response injection allowing a SASL downgrade). MailKit
  is the SMTP *client* the tests drive the relay with — it is not part of the shipped product,
  so no deployed version of GraphMailer was ever exposed to this. The `NoWarn="NU1902"` that
  suppressed the advisory has been removed, so future MailKit advisories surface again.

### Added

- **New “Activity” tab on the Metrics page.** *Recent Activity* used to sit at the bottom of
  *Overview*, where the details of an event opened below the fold. It now has its own tab in
  which the list fills the height and the details are always visible.
- **Live search for Recent Activity.** The box in the card header filters the listed events as
  you type — across every field, including those shown only in the details panel, so an error
  message or a single sender can be found directly. A counter shows how many of the loaded
  events match, and a **✕** in the box clears the search again. The Logs page's search box got
  the same clear button.
- **Details panel for a selected event**, showing what the list no longer carries: size,
  attachment count, receiving listener, TLS, authenticated user, duration and the message id —
  or the failure reason for a failed event. Right-click copies that last line.
- **New “All” folder on the Messages page**, merging queue, failed and sent into one list,
  newest first — and the new default view. An extra **Status** column marks each message as
  *Queued* (amber), *Sent* (blue) or *Failed* (red). The 500-message cap applies to the merged
  result, so the newest messages win regardless of which folder they are in.

### Changed

- **Logs, Messages and the new Activity tab now share one layout.** Each page is a single card
  holding toolbar, list and details, instead of separate cards with a splitter between them.
  The details panel is part of the same surface as the list, opens by selecting a row and
  closes with a **✕** in its top right corner — the *▼ Details* toggle buttons are gone.
- **Details are shown as a label/value raster** with fixed label columns and fixed row heights
  instead of monospaced text with padded labels, in the same typography on all three pages.
  Values that are worth pasting elsewhere (message ids, error texts, log messages incl. stack
  traces) can be copied from the context menu. Empty fields show an em dash, so the raster no
  longer shifts depending on which fields a message happens to have.
- **The ↺ Refresh button on the Metrics and Messages pages is gone.** Both pages reload
  themselves every five seconds and offer no way to pause that, which left the button without
  a job. The Logs page keeps its Refresh button — there it works together with the
  *Auto-refresh* checkbox, which can freeze the view.
- **Upgraded the Microsoft Graph SDK from 5.105.0 to 6.2.0** (Graph.Core 4.0.1, Kiota 2.0.0).
  No functional change — mail delivery, sender directory lookups, the Entra setup wizard and the
  permission self-check all use the same SDK surface as before, and the upgrade required no code
  changes. Operators should see identical behaviour; the install grows by the
  `Microsoft.IdentityModel.*` assemblies that Graph.Core 4 brings along.
- **Kept the remaining runtime dependencies current**: Serilog 4.4.0, Serilog.Sinks.File 7.0.0,
  Serilog.Sinks.Console 6.1.1 and Microsoft.Extensions.Hosting.WindowsServices 8.0.1. Logging,
  service lifecycle and configuration behave as before. Test tooling (xunit, NSubstitute,
  coverlet, FluentAssertions, the test SDK) was updated in the same round without touching the
  shipped product.

### Fixed

- **Column widths in Recent Activity and the log list no longer snap back.** Every automatic
  refresh replaced the list contents, which made WPF re-apply the column layout and discard any
  width the user had dragged. The widths (and the selected row) now survive a refresh. The
  *Subject* column additionally moved to the end of the grid: a proportional column between
  fixed ones is re-measured to “whatever space is left” on every layout pass, which no saved
  value could override.
- **Recent Activity now honours the time range selector.** It listed the newest 200 events
  regardless of the selected 24h/7d/30d/90d range, while every other section of the page
  filtered correctly.
- **The rounded bottom corners of the list cards are no longer covered** by the grid painting
  its background over them.

## 1.2.3.1013 — 2026-07-21

### Added

- The scheduled **operations report** now ends with a **Recommendations** box listing the
  optional features that are switched off on this machine — the weekly **update check** and
  the anonymous **usage telemetry** — with one sentence each on what they do and what is (not)
  transmitted. Purely informational: neutral styling, no health warning, and the box is
  omitted entirely once both are enabled.

### Changed

- The ConfigTool's **Monitoring** page now starts with the **Update Check** and **Anonymous
  Usage Telemetry** cards (previously in the middle of the page, below the self-checks).
  Both are off by default and easy to miss when they sit further down. The help page mirrors
  the new order.
- **Relayed mail now appears in the sender's "Sent Items"** in Exchange Online. Messages
  delivered via `sendMail` were previously sent with `saveToSentItems=false`, so nothing was
  left in the sender's mailbox — only mail with attachments ≥ 3 MB (draft + upload session)
  showed up there. Every message received over SMTP now keeps a copy, matching what users
  expect from a mailbox they send as. No additional Graph permission is required
  (`Mail.Send` already covers it). Service-generated mail (NDRs, admin notifications, the
  operations report, the ConfigTool test mail) is unchanged and still leaves no copy.
- The SMTP **session summary log line** now includes the name the client announced in
  `HELO`/`EHLO` (`helo=…`, `helo=(none)` if the client never greeted). This makes individual
  clients distinguishable when several sit behind the same source IP (NAT, load balancer,
  application server). The value is client-controlled and therefore capped at 255 characters.

## 1.2.2.1012 — 2026-07-20

### Changed

#### All system emails now use a unified HTML template
- Every mail the service (and ConfigTool) generates is now styled like the existing HTML
  operations report — same header, severity banner (green/blue/yellow/red), details table
  and footer — instead of unformatted plain text:
  - all **admin notifications** (certificate expiring/expired, low disk space, IP blocked,
    auth-failure alert, Graph API error/restored, config-decryption error, backup result,
    update available, port outage/restored, service start/stop, batched delivery failures);
  - **NDRs (bounces)** — sent as multipart/alternative with a plain-text fallback, so
    legacy applications that parse bounce text keep working;
  - the **emailed configuration backup** (file name + size shown in the details table);
  - the ConfigTool's **Graph API test email**.
- The "Update available" notification now renders the release link as a button.

### Added

#### Deep mail-traffic statistics (metrics.db schema v2)
- The **Metrics page** in the ConfigTool is now split into five tabs — **Overview, Reception,
  Delivery, End-to-End, Server** — with a global time-range selector (24h/7d/30d/90d) and much
  deeper insights into how the relay is used:
  - **Reception**: SMTP session counts, TLS/auth share, sessions **aborted without QUIT broken
    down by protocol stage** (before HELO / after EHLO / after AUTH / after MAIL…) — makes
    monitoring probes that connect, authenticate and drop immediately visible and countable;
    **rejection counters by reason** (IP blacklist, missing whitelist entry, dynamic IP block,
    failed auth, blocked sender/recipient, size limit, …); top client hosts with an
    aborted-share flag; per-listener breakdown; Ø recipients (To/CC/BCC) and attachment share.
  - **Delivery**: first-try rate, retry histogram (attempts until success), delivery-variant
    split (`sendMail` vs. draft + upload session for large attachments), top failure causes
    grouped by Graph error.
  - **End-to-End**: queue latency Ø/P95/Max (SMTP receipt → Graph delivery), message funnel,
    permanent vs. expired failures.
  - **Overview**: received/delivered/failed/success rate, per-day (or per-hour) mail-flow
    chart, volume, unique senders, queued-now; the Recent Activity table gains attachment
    count, receiving listener, TLS and auth-user columns.
  - **Server**: the existing Memory/CPU/Disk charts (now following the global time range)
    plus the metrics database size.
- **New data recorded** (metrics.db migrated to **schema v2**, idempotent, existing data kept):
  - per received mail: CC/BCC counts (BCC derived from envelope vs. headers), attachment
    count/bytes, receiving listener port, TLS, authenticated user;
  - per delivered mail: retry count, queue latency, delivery variant, attachment count/bytes;
  - per failed mail: retry count and permanent-rejection flag;
  - per SMTP session: hourly aggregate buckets (client IP, listener, outcome clean/aborted/
    faulted/cancelled, last protocol stage, TLS, auth, duration) — no per-session rows;
  - per rejection: hourly aggregate buckets by reason, client IP and listener.
  The service's own health-probe connections are excluded. Retention follows the existing
  `Metrics.RetentionDays`; no new configuration keys.
- **Session summary log line**: when an SMTP session ends, the service now logs one
  Information line with outcome, last protocol stage, TLS, authenticated user, the command
  sequence and duration (replaces the bare "Session completed" line) — client aborts such as
  monitoring probes are now directly readable in the log.
- **Telemetry heartbeat** (opt-in, unchanged mechanism) now includes the new aggregates as
  anonymous counters: sessions total/aborted/faulted/TLS/authenticated, rejection counts by
  group, mails with attachments, delivered first-try/after-retry/via-upload and average queue
  latency. Counters only — still no IPs, addresses or usernames.

## 1.2.1.1009 — 2026-07-19

### Added

#### Anonymous usage telemetry (opt-in)
- The service can send **one anonymous daily report** to the developer's Application Insights
  instance to reveal the install base, version distribution and field errors. Fully opt-in:
  **Monitoring → Anonymous Usage Telemetry** in the ConfigTool (config key
  `Telemetry.Enabled`, default off) — while disabled, nothing is collected and nothing leaves
  the machine.
- **Heartbeat contents**: random install id (GUID, not derived from hardware/user/network),
  GraphMailer version, OS + .NET runtime version, uptime, received/sent/failed mail counts
  since the last report (from the local metrics DB) and the configuration shape (listener
  count, TLS/auth/archiving enabled). **Error reports** (log level Error/Fatal): exception
  type, stack trace, log message *template* (placeholders, never rendered values), component,
  Graph error code/HTTP status, occurrence count — deduplicated by fingerprint, capped at 50
  distinct errors per day. PII-free by construction: no addresses, hostnames, message content
  or exception messages are ever transmitted; the SDK's hostname tags are scrubbed.
- Transmission state (install id, last/next heartbeat) is persisted in
  `data\telemetry-status.json` and shown on the Monitoring page ("Last transmission").
  A failed transmission retries hourly and keeps its unsent counters/error reports.
- Config schema bumped to **v4** (purely additive: `Telemetry.Enabled`).

#### Weekly update check with admin notification (opt-in)
- The service can check the GraphMailer releases on GitHub once a week and report a newer
  version. Fully opt-in: **Monitoring → Update Check** in the ConfigTool (config key
  `UpdateCheck.Enabled`, default off) — while disabled, no request leaves the machine. The
  check downloads only the public release info from `api.github.com`; nothing about the
  installation is sent, and nothing is installed automatically. A failed check (no internet,
  proxy) is retried the next day instead of waiting a week.
- **Status page: new "Software Update" card** showing the installed version, the latest
  release (with *Up to date* / *Update available* badge and release-notes link), last/next
  check time, and a **Check now** button that asks the running service for an immediate
  check (file-based IPC: `data\update-status.json` + `data\update-check.request`).
- **New admin notification type "New GraphMailer version available"** (Notifications page,
  default off): one email per new release — not a weekly reminder — with both versions and
  the release link.
- **Scheduled operations report: new "Software Update" row** in the Health Checks table,
  mirroring the Status page's Software Update card: *Up to date* (OK), *Update available*
  with both versions (Warning, so it also surfaces in the report's alert summary), or
  Unknown while the check is disabled / has not run / last failed.
- **Sidebar update badge**: while an update is available, a small green pill with the new
  version number appears next to the GraphMailer name at the top of the sidebar — visible
  on every page; clicking it opens the Status page.
- Config schema bumped to **v3** (purely additive: `UpdateCheck.Enabled`,
  `AdminNotifications.NotificationTypes.UpdateAvailable.Enabled`).

### Changed

#### "Health Checks" page renamed to "Monitoring"
- The ConfigTool configuration page **Health Checks** is now called **Monitoring** — the name
  matches what the page actually configures (certificate/disk/port/Graph monitoring, metrics
  recording, update check, telemetry, log level). Renamed everywhere the page is referenced:
  sidebar entry, page title, the "Graph API Health Checks" card (now "Graph API Monitoring"),
  hint texts on the Metrics/Notifications/Status pages, and the help
  (`configuration/health-checks.html` → `configuration/monitoring.html` including all
  cross-links). Pure UI/docs rename — no config keys or behaviour changed.

#### Build number is now a running counter instead of the day-of-year
- The 4th part of the version (e.g. `1.2.0.**1004**`) is now `offset + git commit count`
  instead of "days since 2026-01-01". Every commit bumps it by one, so builds are distinct
  and reproducible from the git history — no more identical numbers for several builds on the
  same day. The derivation now lives once in `tools/Get-BuildFileVersion.ps1` (shared by all
  `build-*.ps1` scripts) and in the `_ResolveBuildNumber` MSBuild target for plain
  `dotnet build`/IDE builds, replacing the previously duplicated date arithmetic.
- A fixed offset of **1000** keeps the counter above the old scheme's highest shipped value
  (`.198`), so the version never appears to move backwards — important because the update
  check compares `FileVersion` as a four-part number.

## 1.2.0.198 — 2026-07-18

### Fixed

#### Release build could ship a stale DLL that crashes the apps (`FileNotFoundException: Microsoft.Data.Sqlite`)
- Release 1.2.0.196 shipped a `Microsoft.Data.Sqlite.dll` with assembly version 8.0.28.0
  while both `deps.json` files (and the compiled-in references) demanded 8.0.29.0 — on any
  target machine the ConfigTool's Status page (and the service's metrics access) then died
  with `FileNotFoundException: Could not load … Microsoft.Data.Sqlite, Version=8.0.29.0`.
  Root cause was a build-pipeline gap, not the target system: the floating package version
  (`8.*`) silently moved from 8.0.28 to 8.0.29 between builds, but the incremental
  ReadyToRun step never invalidated its cached compiled image (`obj\Release\R2R\`), and
  `build-release.ps1` publishes with `--no-build` from those intermediates — fresh
  `deps.json`, stale DLL. Two fixes:
  - `build-release.ps1` now deletes `bin\Release` + `obj\Release` of both projects before
    building, so every release is produced from fresh intermediates.
  - All floating NuGet versions (`8.*`, `5.*`, `1.*`, `4.*`, `2.88.*`) in the Service and
    ConfigTool projects are pinned to exact versions; upgrades are now deliberate edits.

#### ConfigTool: window opened partly off-screen on low resolutions
- On screens smaller than the designed window size (1180×860 — e.g. low-resolution server
  consoles or RDP sessions), the ConfigTool opened partly outside the visible area, and a
  minimum size larger than the screen prevented shrinking it back into view. The window
  now clamps its start and minimum size to the available work area (screen minus taskbar)
  and opens centered, so it is always fully visible; the pages scroll when the window is
  smaller than designed.

## 1.2.0.196 — 2026-07-16

### Added

#### Stack traces for troubleshooting (service + ConfigTool)
- **ConfigTool: new diagnostic log** `C:\ProgramData\GraphMailer\logs\configtool-*.log`
  (rolling daily, 7 days retention). Every error the ConfigTool shows in its UI — service
  control actions, health checks, backup/restore, Entra setup, Graph API test send,
  certificate creation, config load/save — is now also persisted with the full exception
  detail (stack trace, inner exceptions); the UI messages are unchanged. Periodic checks
  (status poller, health rows) log de-duplicated so a recurring failure appears once, not
  every 5 seconds.
- **ConfigTool: crash handling hardened.** Unobserved task exceptions (fire-and-forget
  handlers) are now caught and logged; the crash log moved from the install directory to
  the logs directory (`configtool-crash.log`, install dir remains the fallback).
- **Service: Error-level log entries always carry the stack trace.** Five catch sites
  (queue write failure, SMTP password decryption, password capture persistence, TLS
  private-key access, Graph authentication) logged only the exception message; they now
  attach the exception so Serilog writes the full trace to `graphmailer-*.log` /
  `error-*.log` at any configured log level.

#### End-user help (HTML documentation, bundled with the installer)
- New **Markdown-based user help** for administrators (not developers): a Quickstart, the
  post-install / Entra setup path, and one reference page per ConfigTool screen
  (Servers & TLS, Access Control, IP Filtering, Graph API, Mail Queue, Health Checks,
  Notifications, Backup & Restore, plus Status / Metrics / Messages / Logs and a
  Troubleshooting / FAQ / Glossary section) — 19 pages under `docs/help/`.
- **Design matches the HTML operations report** (GitHub-Primer palette, Segoe UI + Consolas),
  with GitHub-style callouts (`> [!NOTE] / [!TIP] / [!IMPORTANT] / [!WARNING] / [!CAUTION]`),
  a shared sidebar mirroring the ConfigTool navigation, breadcrumb and prev/next links, and
  relative links so the HTML works offline by double-click.
- New **`build-help.ps1`** (project root, alongside `build-release.ps1` / `build-installer.ps1`):
  renders the Markdown to standalone HTML via Markdig (cached in `%TEMP%`), with the page order
  and grouping in a single `$SiteMap`. The help carries **no own version** — its footer shows the
  same version as the EXEs (FileVersion derived identically), passed in by the installer build.
- **`build-installer.ps1` now also builds the help**, so a final installer always bundles
  program + service + help. The help tree is published under `INSTALLFOLDER\help` and harvested
  by the MSI; the Start-menu folder gains a **"GraphMailer Help"** shortcut that opens the start
  page (`help\index.html`) in the default browser. This replaces the earlier docs-link placeholder.
- **ConfigTool: in-app help.** An unobtrusive help icon in the toolbar (same place on every page)
  and the **F1** key open the help page that matches the current screen — Status → Status, Servers
  & TLS → Servers & TLS, etc. (`ApplicationCommands.Help`, opens the bundled `help\…` HTML in the
  default browser; a friendly notice appears if the help is absent, e.g. an uninstalled dev build).

#### Sensible out-of-the-box defaults (listeners, TLS certificate, IP whitelist)
- A fresh install now starts with **industry-standard SMTP listeners** — port 25 (plain),
  465 (implicit TLS) and 587 (STARTTLS), with authentication optional on the encrypted
  connectors — instead of a single plain listener on 2525.
- On first start (when no `graphmailer.json` exists) the service **generates and binds a
  self-signed `GraphMailer SMTP` certificate** so the TLS listeners work immediately. The
  certificate-creation logic is now shared between the service and the ConfigTool's
  "Create self-signed certificate" button. If certificate creation is unavailable (e.g. a
  non-elevated dev run) the TLS listeners fall back to plain with an error log, as before.
- The default **IP whitelist is pre-filled with the private address space** (RFC 1918,
  loopback, IPv6 unique-local and link-local), so the relay only accepts internal senders
  out of the box.
- These array-shaped defaults are materialised into `graphmailer.json` on first run rather
  than shipped in `appsettings.json`, avoiding the `IConfiguration` index-merge pitfall where
  a shorter user list would otherwise leak trailing default entries.
- Disabled listeners (`Enabled: false`) are now actually skipped by the service — previously
  every configured listener started regardless of the flag.

#### Single source of truth for defaults
- The ConfigTool no longer carries its own hard-coded defaults: `ConfigService` now overlays
  the user's `graphmailer.json` on top of the bundled `appsettings.json`, so a field the user
  omits inherits the same default the service uses. This also fixes a drift where the backup
  frequency defaulted to `Daily` in the ConfigTool but `Weekly` in `appsettings.json`. The
  `NdrNotifications` and `SenderValidation` defaults are now present in `appsettings.json` too.



#### Installer: firewall exception, launch-on-finish, docs-link placeholder
- The MSI now registers an **inbound Windows Firewall exception** bound to the service
  executable (`GraphMailer SMTP Relay`, program-based — no fixed port), so SMTP connections
  on any configured listener port are allowed without manual firewall rules. The rule is
  removed again on uninstall (`WixToolset.Firewall.wixext`).
- The setup bootstrapper now shows a **"Launch" button** on the success page that starts the
  ConfigTool after an interactive install (suppressed under `/quiet` and `/passive`).
- Start-menu entry stays in the existing `GraphMailer` subfolder; added a code placeholder for
  a future documentation link.

### Added

#### Failed-mail retention (`MailQueue.FailedEmailRetentionDays`)
- New setting on the **Mail Queue** page: permanently failed messages in `mail\failed\` are now
  deleted by the hourly cleanup after a retention period (default **60 days**, `0` = keep forever
  — the previous behaviour). Without a bound the folder grew indefinitely. Part of config schema
  v2 (additive).

#### Message fidelity: inline images, priority, custom headers, threading
- **Inline (CID) images** are forwarded with their `ContentId` and `IsInline` flag — previously
  `<img src="cid:…">` references broke and embedded images appeared as visible attachments. Works
  on both delivery paths (direct sendMail and draft + upload session).
- **Importance/priority** is mapped to Graph (`Importance` header, `X-Priority` as fallback).
- **Custom `X-*` headers** are forwarded (Graph only permits custom headers with the `x-` prefix;
  transport-reserved `x-ms-exchange-*` headers are skipped).
- The **original `Message-ID`** is preserved (`internetMessageId`) and **`In-Reply-To` /
  `References`** are forwarded via the underlying MAPI properties, so reply threading on the
  recipient side keeps working. Verified end-to-end against the real Graph API by a live test.

#### Fail-closed TLS option (`Certificate.FailClosed`)
- New option on the **Servers & TLS** page: *"Do not start TLS listeners without a certificate
  (fail-closed)"*. Default off — the existing behaviour (TLS listener falls back to plain SMTP
  with an error log when the certificate is missing) is unchanged. When enabled, such a listener
  is **not started at all**: the encrypted port stays closed until a certificate is available, so
  credentials and mail content can never be transmitted in cleartext by clients using
  opportunistic STARTTLS. Requires a service restart; documented in the Servers & TLS help page.
  Config schema bumped to **v2** (additive migration).

### Fixed

#### Operations report: "Avg delivery" card showed "no data" instead of the peak
- The emailed report's "Avg delivery" card could show a valid average (e.g. "298 ms") with
  "no data" underneath where the peak belongs. Root cause: `SELECT MAX(duration_ms)` returns
  the value with the column's INTEGER affinity (a `long`), while `AVG` always returns a
  `double`; the collector's scalar reader only accepted `double` and silently dropped the
  peak. It now accepts both numeric types. Same latent risk removed for the memory/CPU peak
  values. Regression test added.

#### ConfigTool could open a configuration but not save it (encryption failure)
- Saving in the ConfigTool failed with *"An error occurred while trying to encrypt the
  provided data"* whenever a secret had to be (re-)encrypted — e.g. entering a backup
  password. Root cause: `DataProtectionExtensions.BuildConfigProtector()` disposed the
  Data Protection `ServiceProvider` (a `using`) before returning the protector; the
  protector then resolved its key-ring services lazily from the disposed provider on
  `Protect()`, throwing `ObjectDisposedException` (wrapped as the DPAPI error). Loading
  worked because decrypting existing values does not hit that path — which is exactly
  why the tool could display but not save. The backing provider is now kept alive for
  the process lifetime. Regression tests added.

#### Quarantined messages no longer disappear silently
- Messages moved to `mail\failed\` because of a corrupt meta file, a missing or an unreadable
  `.eml` were only logged — the SMTP client had already received `250 OK`, so the mail vanished
  from every external viewpoint. All quarantine paths now record the failure in the metrics,
  notify the admin, and send an NDR when the sender is known (NDRs themselves stay exempt — loop
  guard).

#### Rotating the Graph client secret no longer requires a service restart
- The cached Graph client was keyed only on tenant/client-id/auth-method: rotating the client
  secret (or switching the configured certificate) via config reload kept the stale credential
  failing until a restart — a delivery outage the reload should have fixed. The cache key now
  includes a credential fingerprint (SHA-256 of the secret / the certificate selector), so the
  client is rebuilt on rotation. Certificate selection by subject name still picks up a newly
  installed certificate only on restart.

#### Messages with more than 500 recipients fail fast with a clear NDR
- Exchange Online rejects messages with more than ~500 envelope recipients; the relay now
  pre-validates and fails such messages immediately as permanent (clear NDR text) instead of
  making a doomed Graph call.

#### Queue is processed in arrival order (FIFO) with a per-send timeout
- The queue was processed in GUID-filename order — effectively random. Messages are now delivered
  in arrival order (by receipt time). Processing stays deliberately sequential to preserve this
  ordering. A hung Graph send is additionally bounded by an explicit 15-minute per-send timeout
  (generous, because large-attachment uploads legitimately take minutes) and then follows the
  normal retry schedule instead of stalling the queue.

#### Corrupt graphmailer.json no longer keeps the service down
- A syntactically corrupt config file (e.g. a truncated hand-edit) threw during configuration
  load on **every** start — the service stayed down until manually repaired. The file is now
  quarantined to `graphmailer.json.corrupt-<timestamp>` with a clear error log, and the service
  starts with built-in defaults (first-run provisioning re-seeds the listeners). Rename the
  repaired file back to restore the configuration.

#### Data Protection key-ring fallback is no longer silent
- When the registry key ring is inaccessible, the fallback to file-based keys was silent — every
  `ENC[...]` secret then failed to decrypt and only the *symptom* was reported. The fallback now
  logs an error naming the cause and the consequence.

#### Housekeeping: metrics.db compaction and bounded migration backups
- `metrics.db` never shrank: the retention cleanup deleted rows but the file only grew. The
  cleanup now runs `VACUUM` and truncates the WAL after deleting rows.
- Config schema-migration backups under `config\backups\` are now pruned to the newest 10.

#### IP blocking: periodic global sweep
- Failure histories were only pruned when the *same* IP caused another event — one-off source
  addresses (trivially rotated over IPv6) kept a dictionary entry forever. A 10-minute sweep now
  evicts expired blocks and stale failure histories globally.

#### ConfigTool: startup crash no longer leaves a window-less zombie process
- The UI-thread exception handler set `Handled = true` to keep the app alive so the user
  could read the message — correct while running, but a crash **during startup** (before
  the main window loaded) then left a windowless process holding the single-instance
  mutex, blocking every restart until killed via Task Manager. The handler now detects
  the not-yet-started case and exits cleanly (releasing the lock) after showing the error.
  (Fixed alongside the crash trigger below — a validation handler that ran during XAML
  initialization before its dependent elements existed.)

#### Missing notification sender is now a visible, save-blocking error
- Graph app-only authentication needs an explicit sender mailbox — there is no fallback
  account. A configuration without `AdminNotifications.SenderAddress` silently disabled
  **four** features (admin notifications, NDRs, scheduled reports, emailed backups): the
  service skipped every send with a log-only warning, while the ConfigTool saved the
  state without complaint (only the address FORMAT was checked, and even that didn't
  block saving). Now: a red error under the sender field on the Notifications page
  whenever something on that page depends on it, plus a save-time gate over the full
  collected configuration (covers the Backup page's email toggle) that names the
  affected features. An invalidly formatted sender also blocks saving.

#### Mail Queue page: retention fields no longer indented
- On *Archiving & Cleanup*, the "Retention (days)" input and its description were visually
  indented compared to the other entries: the fixed-width TextBox was centered inside its
  auto-sized column instead of left-aligned. Both retention rows (sent + failed) now share a
  fixed label column — fields sit flush at the card edge, hints align vertically.

#### Backup page: dead configurations are now visible errors (password, email recipients)
- The ConfigTool allowed saving an **enabled** backup schedule with **empty** password fields —
  a configuration that looks functional but never produces a backup: the service pauses
  scheduled backups without a password and only logs a warning. The Backup page now shows the
  same red validation error used for short/mismatched passwords ("A password is required for
  scheduled backups …"), re-evaluated when the schedule is toggled. Manual backups were already
  guarded. Documented on the Backup & Restore help page.
- Same class of gap for **email backups**: the toggle could be enabled and saved with an
  **empty recipient list**, and the service silently skips the email step in that state —
  not even a log line. The page now shows a red validation error ("Add at least one
  recipient …"), re-evaluated when the toggle or the recipient list changes. Both errors
  block **Save** (the existing validation gate now covers them).
- The **cross-page dependency is now visible where it bites**: emailed backups are sent
  from the sender address configured on the *Notifications* page, but nothing on the
  Backup page said so beyond a subtle subtitle. The Backup page now queries the sender
  live and shows a red error pointing to the Notifications page when email backups are
  enabled without one; conversely, the Notifications page's sender error now also lists
  "emailed backups" when only that feature depends on it. Both pages re-validate when
  they become visible, so fixing the value on one page clears the error on the other.

#### Config cleanup: dead `Api` section and never-binding keys removed
- The entire **`Api` section** in `appsettings.json` — a leftover from the Node.js
  predecessor (web dashboard on port 3000/3443 with a default password `"admin"`) that no
  code has ever read — is removed. It contradicted the "no web server, no open port"
  architecture and looked alarming in security reviews.
- Two never-binding keys removed (`CertificateExpiringWarning.DaysBeforeExpiry` — the real
  threshold is `CertificateMonitoring.WarningThresholdDays` — and
  `GraphApiConnectionError.DelayBeforeNotificationSeconds`), and the misnamed
  `IpBlockedAlert.BlockedAttemptsThreshold` is renamed to `FailureThreshold` so it actually
  binds (same value as the code default — no behaviour change).

#### Readable message metadata: proper header parsing
- Subject and Message-ID for the queue metadata (logs, metrics, ConfigTool Messages page)
  are now extracted with MimeKit's header parser instead of a manual 8 KB scan: RFC 2047
  encoded-word subjects (`=?utf-8?B?…?=`) are decoded to readable text, messages with
  large Received/DKIM header blocks no longer lose their subject, and `Subject:`-looking
  lines in the body can no longer be mis-extracted. Metadata only — delivery always uses
  the raw bytes.

#### Housekeeping & logging polish
- **Orphaned-eml cleanup runs hourly** (previously only at startup), with a 5-minute grace
  period so a message whose meta rename is still in flight is never quarantined by mistake.
- **Log rolling**: a day exceeding the 100 MB log size limit now rolls to a `_001` file
  (`rollOnFileSizeLimit`) instead of silently dropping the rest of that day's output.
- **`Smtp.MaxSizeBytes` above ~2 GB** (the library's int limit) is now clamped **with a
  warning** naming the difference between the configured and the advertised EHLO SIZE.

#### Password-capture window is logged as a warning
- While `CaptureNextPassword` is armed, **any** password authenticates that user. Each such
  authentication is now logged at Warning (previously Information) — the log is the operator's
  only signal that the capture window is open.

#### Permanent Graph rejections no longer retry for 24 hours before the NDR
- Every Graph error was treated as transient, so permanently hopeless deliveries — invalid
  recipient, sender mailbox not found (404), request too large (413), recipient limit exceeded,
  and the hybrid-tenant `MailboxNotEnabledForRESTAPI` — churned through the full
  `MessageExpirationHours` window (default 24 h) before the sender got an NDR.
  `GraphApiClient` now classifies rejections (new `GraphDeliveryException.IsPermanent`,
  conservative: auth/permission problems and throttling stay transient because an operator can
  fix them within the window), and `QueueProcessor` fails permanent rejections immediately —
  NDR after seconds instead of a day. Verified against the real Graph API by a live test.

#### NDRs are now queued and retried instead of one-shot via Graph
- NDRs were sent directly via Graph exactly once, with all errors swallowed. Since a permanent
  delivery failure usually coincides with a Graph outage, the NDR typically failed too and the
  sender never learned the mail was lost. NDRs (sender + admin copy) are now written into the
  service's own mail queue as regular messages and inherit the full retry schedule. A new
  `IsNotification` marker in the queue metadata prevents NDR-for-NDR loops: a permanently
  failing NDR is quarantined with an admin notification, but never generates another NDR.

#### Messages with several small attachments could exceed Graph's 4 MB request cap
- The sendMail-vs-upload-session routing decided per single attachment (≥ 3 MB), ignoring the
  total: a message with e.g. three 1.5 MB attachments stayed on the direct path and was rejected
  by Graph's hard 4 MB request cap (and, before the classification fix above, retried for 24 h).
  The router now estimates the total encoded request size (body + attachments × 4/3 base64
  overhead) and moves the largest attachments to the upload-session path until the direct
  payload fits.

#### Large-attachment uploads are now resumable and throttling-aware
- Upload-session chunks were sent through a bare `HttpClient` without the Graph middleware —
  no 429/`Retry-After` handling, no 5xx retry, and any single chunk failure restarted the whole
  upload from byte 0. The upload now uses the Graph SDK's `LargeFileUploadTask` (slices go
  through the standard retry pipeline) and resumes an interrupted upload once at the server's
  `NextExpectedRanges` instead of restarting. Verified by the live upload-session test.

#### Failed AUTH no longer blocks the session after a successful re-authentication
- The `Auth:Failed` session flag set by a failed AUTH was never cleared, so a client that
  mistyped its password once and then authenticated correctly **on the same connection** stayed
  blocked at MAIL FROM for the rest of the session. The flag is now removed on successful
  authentication. Regression test added.
- After Microsoft Graph accepted a message there was a window (crash, power loss, or a
  normal service stop) in which the queue files were not yet removed — on the next start
  the message was **sent again** and the recipient received it twice. Two-part fix in
  `QueueProcessor`: the post-send commit (metrics, archive/delete) no longer runs on the
  shutdown token, so a service stop can no longer interrupt it; and the delivery is now
  persisted to the meta file (`SentAt`, written atomically) **before** the cleanup, so a
  message whose commit was interrupted by a hard crash is recognised on the next pass and
  its cleanup is completed **without re-sending**. Regression tests added.
- The retry-schedule update in the queue meta file is now written atomically
  (temp + rename, like the initial queue write) — a crash mid-write could corrupt the meta
  and quarantine a still-deliverable message.

#### SMTP client received 554 for a successfully queued message (duplicate delivery)
- The metrics write ran inside the same `try` as the queue write: when recording the
  received-mail metric failed (e.g. SQLite briefly locked) **after** the message was already
  durably queued, the client got `554 Transaction failed`, re-sent the message, and the
  recipient received it twice. The metrics call is now telemetry-only: once the message is
  queued, the client always gets `250 OK`. Regression test added.

#### Temporary disk problems caused silent mail loss (554 instead of 4xx)
- A failed queue write (disk full, IO error, ACL problem) was answered with the **permanent**
  `554 Transaction failed` — conforming SMTP clients discard the message on 5xx, so a
  temporary local condition silently lost mail. The relay now answers with the **transient**
  `451 Requested action aborted: local error in processing`, so the client keeps the message
  and retries. Regression test added.

#### One faulted background worker stopped the whole service — and nothing restarted it
- The host ran with .NET's default `BackgroundServiceExceptionBehavior.StopHost`: a single
  unhandled exception in any of the ~13 background workers (e.g. a monitoring service) took
  down SMTP relaying and queue processing with it. The host now uses `Ignore` (the fault is
  still logged), `QueueProcessor` additionally guards each polling/cleanup tick so one failed
  batch cannot end delivery for the process lifetime, and the shutdown timeout is set
  explicitly (30 s).
- Neither the CLI install (`GraphMailer.exe --install`) nor the MSI configured **service
  recovery actions**, so a crashed service stayed stopped until an operator intervened.
  Both now register restart-on-failure (60 s delay, up to three restarts, failure counter
  resets daily): `sc.exe failure` in `ServiceManager.Install`, `util:ServiceConfig` in the
  MSI.

#### A single invalid listener port crashed the whole service
- An out-of-range `Port` (e.g. `0` or `70000`) or a duplicate port in the `Servers` config
  threw during listener construction and terminated the service. Listeners are now validated
  and built individually: an invalid or duplicate entry is skipped with an error log naming
  the listener and the reason, while the remaining listeners start normally. Regression
  tests added.

#### Installer: fewer interruptions on install / upgrade
- **Unnecessary .NET runtime reinstall removed.** The bootstrapper detected the runtime by
  checking for the *exact* build-time version folder (e.g. `…\Microsoft.WindowsDesktop.App\8.0.28`).
  A machine that already had a usable but older `8.0.x` (e.g. 8.0.18) reinstalled the **shared**
  .NET Desktop Runtime, which triggers Restart Manager and asks the user to close unrelated running
  .NET apps (e.g. PowerToys). Detection is now **version-tolerant** via `netfx:DotNetCoreSearch`
  (any installed .NET 8 Desktop Runtime x64 satisfies it; a net8.0 app runs on any 8.0.x), so the
  runtime is installed only when truly absent.
- **ConfigTool no longer triggers the slow "Files In Use" prompt.** On install/upgrade the MSI now
  closes a running ConfigTool up front via `util:CloseApplication` (WixCloseApplications), instead
  of the user having to wait for the generic Restart-Manager prompt and close it manually. The
  service is released by its existing `ServiceControl` stop. (Requires the `WixToolset.Util.wixext`
  extension on the MSI build, now passed by `build-installer.ps1`.)

#### Default notification subject prefix differed between service and ConfigTool
- The service defaulted the alert/report subject prefix to `[SMTP Alert]`
  (`AdminNotificationsOptions` + `appsettings.json`) while the ConfigTool defaulted to
  `[GraphMailer]` (`ConfigDocument` + `ConfigService`), so an unconfigured `graphmailer.json`
  produced a different subject than the ConfigTool advertised. Both sides now default to
  `[GraphMailer]` (the product brand).

#### ConfigTool showed different default listeners than the service
- On a fresh install the service binds the listeners from the bundled `appsettings.json` (a
  single `Plain SMTP` listener on port **2525**), but the ConfigTool's SMTP page displayed its own
  hard-coded defaults (three connectors on **25 / 465 / 587**) whenever the user config had no
  listeners — so the tool advertised a setup the service never ran. The ConfigTool now reads the
  listener defaults from the same `appsettings.json` the service uses
  (`ConfigService.ReadDefaultServers`), so the out-of-the-box view matches what the service
  actually starts. Tests added

#### Service failed to start on a fresh install (missing config directory)
- On a clean machine (no `%ProgramData%\GraphMailer`), the service crashed at startup with
  `DirectoryNotFoundException` before any logging or folder creation, so the installer's
  service-start timed out and rolled back. Cause: `AddEncryptedJsonFile` builds a
  `PhysicalFileProvider` (for `reloadOnChange`) rooted at the config directory, whose constructor
  throws when that directory does not exist yet. It now creates the directory first. Regression
  test added
- Hardening (shared Data Protection key ring): the key ring is now protected with **machine-wide
  DPAPI** (`ProtectKeysWithDpapi(protectToLocalMachine: true)`) so the **LocalSystem** service and
  the elevated (admin-user) ConfigTool reliably share the same registry keys, and
  `PersistToRegistryOrFallback` catches `SecurityException` / `IOException` (not only
  `UnauthorizedAccessException`) so a non-privileged process falls back to file keys instead of
  crashing. _Existing dev machines with a key ring from an older build: delete
  `HKLM\SOFTWARE\GraphMailer\DataProtection` once before reinstalling._

#### Queue head-of-line blocking (newly queued mail never delivered)
- `QueueProcessor` selected each batch with `EnumerateFiles().Take(BatchSize)` — the first
  *BatchSize* files by GUID filename. Messages still inside their exponential back-off
  window were skipped **but still consumed a batch slot**, so once *BatchSize* messages at
  the front of the queue were in back-off, every later message (e.g. freshly queued mail)
  was never even attempted — its `RetryCount` stayed at 0 indefinitely. The batch now
  counts only messages it actually attempts, scanning past back-off entries, so ready mail
  is always processed regardless of how many earlier messages are waiting to retry.
  Regression test added

### Added

#### Config & database schema versioning (forward-only migrations)
- `graphmailer.json` now carries a top-level **`SchemaVersion`**. On an application upgrade the
  service (at startup) and the ConfigTool (on open) migrate an older file up to the version the
  build understands — backing up the original to `config\backups\` first — so renamed/removed
  keys are handled instead of silently dropped. A config written by a **newer** build is used
  as-is with a warning (the ConfigTool shows a dialog). First migration (v0→v1) removes the
  obsolete `MailQueue.MaxRetries` / `RetryDelaySeconds` keys left by the retry-policy change
- `metrics.db` is now versioned via SQLite **`PRAGMA user_version`** (`MetricsService`). Existing
  databases are upgraded by **idempotent, forward-only** steps in a transaction-safe path —
  e.g. the `client_ip` column is added automatically, so an old database no longer has to be
  deleted. A database newer than the build is left as-is with a warning
- New `ConfigSchema` / `ConfigMigrator`; `MetricsService.ApplyMigrations`. The greenfield
  "change schemas directly" rule is retired — schema changes now bump a version + add a tested
  migration step (see CLAUDE.md)

#### Windows installer (MSI + bootstrapper)
- New WiX-based installer: a per-machine **MSI** plus a **setup.exe bootstrapper** that chains
  the **.NET 8 Desktop Runtime** prerequisite (downloaded on demand, so the package stays small;
  skipped when already present). Installs Service + ConfigTool to `Program Files`, **registers
  the `GraphMailer` Windows service** (auto-start, LocalSystem) directly from the MSI, and adds a
  Start-menu shortcut for the ConfigTool. Data stays in `ProgramData` as before. Branded with the
  app icon (setup.exe + Add/Remove Programs) and the GraphMailer logo in the setup window —
  `tools\generate-icons.ps1` now also emits `installer\graphmailer.ico`
- **Silent install/uninstall** supported: `GraphMailerSetup-<ver>.exe /quiet /norestart` and
  `… /uninstall /quiet /norestart` (also `/passive`, `/log`); or `msiexec /i|/x … /qn`. Major
  upgrades replace the previous version automatically (stable `UpgradeCode`)
- New `installer/` (`Package.wxs`, `Bundle.wxs`, `README.md`) and `build-installer.ps1`
  (publish → MSI → bootstrapper, runtime resolved/downloaded, version stamped from
  `Directory.Build.props`). Requires the WiX 5 tool (`dotnet tool install --global wix`)

#### Standards-aligned retry policy (time-based, two-phase)
- The queue retry behaviour was reworked to follow Microsoft Exchange and RFC 5321 §4.5.4.1
  instead of a short exponential back-off. It is now **two-phase and time-based**: a few fast
  **transient** retries for short blips, then a **steady** interval, until a **message
  expiration** budget elapses (measured from receipt) — only then is the message moved to the
  failed queue and an NDR is sent. Give-up is by **time, not a fixed attempt count**
- New `MailQueue` settings replace `MaxRetries` / `RetryDelaySeconds`:
  `TransientRetryCount` (default 6), `TransientRetryIntervalSeconds` (300 = 5 min),
  `RetryIntervalSeconds` (900 = 15 min), `MessageExpirationHours` (24). Defaults match
  Exchange; the old ~2.5 h window is replaced by a 24 h window (≈ 100 attempts)
- The Mail Queue page shows a **live "Calculated retry schedule"** derived from these settings,
  e.g. *"Retry every 5m for the first 6 retries, then every 15m, until the message expires 24h
  after receipt (≈ 101 attempts total)"*. The formula lives in one place (`RetrySchedule`) used
  by **both** the `QueueProcessor` and the preview, so they cannot drift apart
- The Messages page and report "failed queue" list now show a plain **attempt count** (retries
  are no longer bounded by a fixed maximum)
- **Config note**: this is a `MailQueue` schema change — re-save the configuration in the
  ConfigTool (or update `graphmailer.json`) so the new keys are written

#### Periodic operations report (weekly/monthly HTML email)
- The service can now send a **scheduled HTML operations report** (weekly or monthly) to
  the **admin notification recipients** (no separate recipient list). Off by default; the
  whole report is toggled by a single switch
- **Contents**: a status banner, mail-queue & backlog counters (failed/queued/delivered),
  a **failed-queue action list** (messages in `mail\failed\` with sender, recipient,
  subject and last error), live **health checks** (SMTP service, config secrets, TLS
  certificate, SMTP ports, disk, queue, Graph API), email **statistics** vs. the previous
  period (delivered, failed, success rate, avg delivery time, distinct senders, volume),
  a **daily-volume chart**, **top senders** and **top sending hosts**, plus system &
  performance figures
- **Design**: minimal, technical, GitHub-Primer styling; Outlook-safe (table layout,
  inline styles). The daily-volume chart is a **server-rendered PNG** (area/line, two series
  on independent scales) embedded as a **CID inline image** — both Outlook Classic and new
  Outlook strip inline SVG, so a raster image is the only chart that renders in both. PNG
  rendering uses **SkiaSharp** (new dependency, platform-neutral). All dynamic text is
  HTML-encoded
- **Scheduling**: default weekly, Monday, 07:00 (also monthly on a day-of-month 1–28).
  The scheduler holds its next-run target across ticks and hot-reloads on config change —
  it does **not** recompute the target every tick (which would never fire)
- New config under `AdminNotifications.ScheduledReport`
  (`Enabled`, `Frequency`, `TimeOfDay`, `DayOfWeek`, `DayOfMonth`). New
  `ScheduledReportOptions`, `ReportSchedule`, `ReportDataCollector`, `HtmlReportRenderer`,
  `DailyChartImage` (SkiaSharp), `ScheduledReportService`, and
  `IGraphApiClient.SendHtmlNotificationAsync` (now supports a CID inline image)
- **ConfigTool → Notifications**: a new "Periodic Reports" card (enable toggle with
  dependent controls disabled when off; frequency switches between day-of-week and
  day-of-month)
- **Metrics**: `email_events` gains a `client_ip` column (recorded for `received` events)
  to power the top-sending-hosts table. No migration path — delete `data\metrics.db` if an
  older database exists; it is recreated on next start

#### Version logged at service startup
- The service now logs its version at startup (e.g. `version 1.1.0.163`), derived
  automatically from the assembly — no manual upkeep. The four-part value equals the
  release folder name, so an operator can confirm at a glance which build is running
  (and spot a stale installed binary). New `BuildInfo` helper

#### Configuration backup & restore (portable, password-encrypted)
- The configuration (including secrets) can now be backed up to a portable, encrypted
  `*.gmbak` file. **Format**: a manifest + config ZIP wrapped in a password-encrypted
  container — PBKDF2-HMAC-SHA256 (600k iterations) + AES-256-GCM, all .NET built-ins, no
  third-party crypto. The container is authenticated, so a wrong password or a tampered
  file fails cleanly instead of producing garbage
- **Portable by design**: secrets are decrypted on backup and **re-encrypted with the
  target machine's key on restore**, so a backup restores on any machine — the registry
  Data Protection key ring is *not* needed (and isn't transportable anyway, being
  DPAPI-bound). The backup is protected by a **separate** password, independent of the
  Microsoft 365 secret
- **Scheduled backups** (service): off by default; **default schedule weekly, Sunday,
  03:00** (also configurable to daily, at a chosen time). Rotation keeps the newest
  *N* backups. Default location `%ProgramData%\GraphMailer\backups`
- **Email backups** (optional): each scheduled backup can be emailed to a dedicated
  recipient list (separate from admin-notification recipients), using the notification
  sender address
- **ConfigTool "Backup & Restore" page**: configure the schedule/rotation/password/email,
  **create a backup now**, and **restore** — file browser (defaults to the backups
  directory), password prompt, a warning before overwriting an existing configuration,
  then overwrite and reload (restart the service to apply)
- **Status notifications**: after every scheduled run the service sends an admin
  notification with the outcome — success (file, size, retention, email status) or
  failure (with the reason). New `BackupResult` notification type (default enabled);
  failures are also logged at `Error`. Comprehensive `[Backup]` logging throughout
- New config section `Backup` (`Enabled`, `Frequency`, `TimeOfDay`, `DayOfWeek`,
  `MaxBackups`, `Directory`, `Password` as `ENC[...]`, `Email.{Enabled,Recipients}`).
  New `BackupCrypto`, `BackupArchive`, `ConfigBackupService`, `BackupSchedule`,
  `BackupBackgroundService`; 32 tests

### Fixed

#### Scheduled backups now actually fire at the configured time
- The scheduler recomputed the "next run" (always a future time) on every loop iteration,
  so the fire moment was never detected — scheduled backups never ran. It now holds the
  target time until it is reached, then runs and reschedules. The next run time is logged
  (`[Backup] Next backup scheduled for …`). Regression covered by `PlanTick` unit tests
- The scheduler now picks up backup-config changes **without a service restart**: an
  options change wakes the sleeping loop and re-schedules immediately (consistent with the
  other hot-reloadable settings; backup settings are intentionally not restart-required)

#### Backup & Restore page polish
- The ConfigTool window is taller so the navigation no longer scrolls
- Backup toggles render correctly (were stretched/misaligned)
- When scheduled backups are disabled the rest of the schedule box is greyed out and
  non-interactive; the day-of-week field is greyed when frequency is not Weekly
- Time of day is validated as **HH:mm** (separate hour 0–23 / minute 0–59 fields) — invalid
  input is rejected; "Keep last N backups" has up/down steppers and a visible 1–365 range
- Backup directory is prefilled with the default path and chosen via a folder picker
  (read-only field, no free-text)
- Backup password now requires confirmation with the same rules as user passwords
  (≥ 8 chars, must match); save is blocked on mismatch
- **"Create backup now" now verifies the file exists** after writing and reports the full
  path and size (previously it could report success without writing); restore's file
  browser opens in the backups directory
- A successful **restore now triggers the "restart required" badge** (a restore replaces
  the whole configuration, so the running service is always out of date until restarted);
  the badge clears automatically once the service restart is detected
- Email recipient validation accepts normal addresses (e.g. `jane.doe@contoso.com`);
  validation centralized in a tested `EmailValidation` helper
- The backup status notification can be toggled on the **Notifications** page
  ("Configuration backup result")

#### ConfigTool no longer fails to load on a single undecryptable secret
- Previously, one `ENC[...]` value that could not be decrypted (e.g. config restored to a
  different machine, or a manually edited secret) made `ConfigService.Load()` throw and
  the ConfigTool started with **all defaults** — the entire configuration appeared empty
- Now the load is resilient: every decryptable value loads normally, only the affected
  field(s) are left blank, and the ConfigTool shows a non-blocking warning naming the
  exact JSON paths (e.g. `Users[0].Password`). Re-enter and save to re-encrypt
- `ConfigDocument.DecryptionFailures` carries the affected paths; the now-unused
  `ConfigCorruptException` was removed
- The service runtime already loaded the rest of the config (the optional config provider
  blanks an undecryptable value and continues); the [startup secret-integrity check]
  added below logs and alerts on it
- ConfigTool: undecryptable secrets are now **marked inline** so they are easy to find — a
  warning under the Graph **Client Secret** field, a warning icon on the affected rows in the
  **Users** grid, and a red dot on the **Graph API** / **Access Control** nav items. The
  markers (including the nav dots) clear when the affected value is re-entered or the config
  is saved. New `DecryptionFailureMap` helper (10 tests)

### Added

#### Startup secret-integrity check
- On startup the service now verifies that every `ENC[...]` value in `graphmailer.json`
  can be decrypted with the current Data Protection key ring (registry
  `HKLM\SOFTWARE\GraphMailer\DataProtection`). Because the user config is loaded as
  *optional*, an undecryptable secret was previously blanked silently and only surfaced
  at the first Graph call or SMTP login — typically after restoring the config to a
  different machine without its key ring
- A mismatch is now reported eagerly: `LogError` listing the affected JSON paths (field
  paths only — never cipher text or secret material) plus an admin notification
  (`ConfigDecryptionError`, enabled by default). The notification goes out via Graph API;
  if the Graph client secret itself is the undecryptable value, the log entry remains the
  signal
- New `SecretIntegrityChecker` (pure scan) + `SecretIntegrityCheckService` (one-shot
  hosted check); 13 unit tests
- ConfigTool: the Status page now shows a **"Config Secrets"** health row that runs the
  same decryptability check locally (shared key ring, no service required) — so an
  operator sees a key-ring/config mismatch **before** starting the service, e.g. right
  after a restore. When a secret cannot be decrypted, a dismissible warning banner
  appears at the top of the page naming the affected values

#### Single-instance enforcement
- The Windows service and the ConfigTool now each refuse to start a second instance,
  enforced machine-wide via a named `Global\` mutex (`GraphMailer.Service` /
  `GraphMailer.ConfigTool`). A second service process (e.g. started manually while the
  Windows service runs) would otherwise compete for the SMTP ports and the mail queue;
  a second ConfigTool would race on `graphmailer.json` writes and the file-based IPC
- The service logs `Fatal` and exits with code 1; the CLI commands
  (`--install`/`--uninstall`/`--status`) are exempt
- The ConfigTool brings the already running window to the foreground instead of opening
  a second window (falls back to an info message box across sessions)
- New `SingleInstanceGuard` (Service Infrastructure); 4 unit tests

#### Microsoft 365 Sender Validation (default off)
- MAIL FROM addresses can now be validated against the tenant directory **before** a
  message is accepted: unknown senders get an immediate `550` at SMTP time instead of
  failing at Graph delivery after 3 retries (~7 min) and landing in `mail/failed/`
- New `SenderValidation` config section (`Enabled`, `RefreshIntervalMinutes`,
  `FailClosed`) — live-reloadable, no service restart required
- **Aliases (proxyAddresses) are valid senders** and are resolved to the mailbox
  owner's Graph object id at send time — previously an alias as envelope sender
  always failed with 404 because `/users/{key}/sendMail` only accepts UPN or object id.
  Shared mailboxes (sign-in disabled) are recognized as valid senders
- Caching: periodic full directory sync (`GET /users` paged, default hourly) plus
  on-demand single-address lookup for cache misses (catches newly created mailboxes);
  negative results are cached for 5 minutes; lookups are bounded and time-limited
  (5 s) so MAIL FROM never hangs on Graph
- **Fail-open by default**: if Graph is unreachable or the permission is missing,
  senders are accepted unvalidated, a warning is logged and one admin notification
  per outage is sent; optional `FailClosed` rejects instead
- Requires the **User.Read.All application permission**; the ConfigTool's Entra
  setup wizard now grants it alongside Mail.Send (existing installations: add the
  permission manually in Entra, or re-run the setup wizard)
- ConfigTool: new "Microsoft 365 Sender Validation" card on the Access Control page
- The card shows the **directory sync status** (last sync time, user/address counts,
  next sync, last error) and offers a **"Sync now"** button. The service writes a
  status file after every sync and picks up a sync-request file dropped by the
  ConfigTool within a few seconds (file-based IPC, same pattern as password capture).
  "Sync now" is only available while validation is enabled **and the service is
  running** (the status line notes "service not running" otherwise)
- New services: `TenantSenderDirectory` (cache), `GraphDirectoryGateway` (Graph
  queries), `SenderDirectorySyncService` (periodic refresh), `GraphClientProvider`
  (GraphServiceClient creation extracted from `GraphApiClient`, now shared)
- Tests: 11 unit tests for the directory cache, 3 config round-trip tests,
  5 SMTP integration tests (Valid/Unknown/Indeterminate × FailClosed matrix)

#### Versioning
- Version properties in `src/Directory.Build.props`: `Version` 1.1.0 (manual SemVer),
  `AssemblyVersion` fixed at 1.1.0.0, `FileVersion` 1.1.0.`<build>` where the build number
  is auto-derived as days since 2026-01-01 (UTC), `InformationalVersion` 1.1.0+`<yyyyMMdd>`
- `build-release.ps1` at project root: publishes Service + ConfigTool as self-contained
  win-x64 Release into a versioned folder `C:\Build\GraphMailer.NET\Releases\<FileVersion>\`
  (cleaned before each build); the build number is computed once and passed to MSBuild
  via `/p:_BuildNumber` so folder name and embedded FileVersion always match

#### Messages page (mail folder browser)
- New read-only **Messages** page in the ConfigTool (Operations section): browses the
  service's mail directories with a folder selector (Queue / Failed / Sent)
- Columns: received timestamp, from, to, subject, status; for queue/failed entries
  additionally attempts (e.g. "2/3" vs. configured MaxRetries), last attempt time,
  last error, and (queue only) next retry time
- Auto-refreshes every 5 s while visible; capped at the newest 500 entries;
  corrupt/mid-write meta files are skipped
- Shows a hint when the Sent folder is empty because "Archive sent emails" is disabled
- Slim grid (received / from / to / subject / attempts) with a collapsible details
  panel (same pattern as the log viewer) for status, client IP, last attempt,
  last error, next retry and message ids; user-resized column widths and the
  current selection survive the 5 s auto-refresh
- Service: `MailMetadata` gained `LastAttemptAt` and `LastError`; the `QueueProcessor`
  now persists the failure time and error message of every delivery attempt into the
  message's meta.json (previously the per-attempt error existed only in the log)
- The non-delivery failure paths (corrupt meta file, missing/unreadable EML,
  orphaned EML) also record a `LastError` so every entry in `mail\failed\` carries
  a reason
- `MailMetadata` gained `SentAt`: the successful Graph delivery time is recorded and
  shown in the Messages details panel. On delivery the stale `NextRetryAt` from
  earlier failed attempts is cleared (it made archived mails look like another
  attempt was pending); permanently failed messages also clear it.
  `LastAttemptAt`/`LastError` are kept as history on delivered messages

#### Live test suite (opt-in, real M365 tenant)
- New test project `GraphMailer.Tests.Live`: 7 end-to-end tests against a real
  test tenant — sendMail delivery, ≥3 MB upload-session delivery, unknown-sender
  rejection, alias→object-id delivery, sender-directory sync and lookups
- Tests are **skipped automatically** when no tenant is configured; normal
  `dotnet test` runs stay green and never touch the network
- Credentials live outside the repository: .NET user secrets (populate via
  `tools\set-live-test-secrets.ps1`, which copies TenantId/ClientId/certificate
  reference from the ProgramData runtime config), a gitignored
  `livesettings.local.json`, or `GRAPHMAILER_LiveTests__*` environment variables (CI)
- New root `.gitignore` (bin/obj, `*.local.json`, runtime folders) in preparation
  for publishing the repository

#### Application icons
- New shared base icon (white envelope on a blue Fluent tile, accent color #0078D4)
  with per-app badges: the Service EXE shows a green play badge, the ConfigTool a
  slate gear badge (also used as the ConfigTool window icon)
- The Service EXE previously had no icon at all
- Generated by `tools\generate-icons.ps1` (multi-resolution ICO, 16–256 px,
  PNG-compressed entries) — re-run the script to change the artwork

### Changed

#### ConfigTool navigation: configuration vs. monitoring
- The sidebar is now split into three visually separated groups: **Overview** (Status),
  **Configuration** (all pages whose changes go through Save/Discard: Servers & TLS,
  Access Control, IP Filtering, Graph API, Mail Queue, Health Checks, Notifications)
  and **Monitoring** (read-only runtime data: Metrics, Messages, Logs) — previously
  the Operations group mixed editable settings with read-only viewers
- Monitoring nav items use green-tinted icons to signal "live runtime data";
  configuration items keep the neutral gray
- The **Monitoring settings page was renamed to "Health Checks"** (it configures
  certificate/disk/port/Graph API health monitoring) to avoid colliding with the
  new Monitoring nav group
- The **Logging card (log level) moved from the Metrics page to the Health Checks
  page** — it was the only editable setting on a monitoring page. Metrics is now
  purely read-only and no longer participates in Save/Discard

### Security

- **Disabled SMTP users could still authenticate**: the runtime `UserEntry` was missing
  the `Enabled` property, so the flag written by the ConfigTool was silently dropped
  during options binding and never checked. `AuthHandler.ValidateUser` now rejects
  disabled users (incl. capture mode); a regression binding test guards the contract

### Improved logging (rejection reasons)

- SMTP rejections now name the rule that caused them:
  - Sender/recipient filter: `matches block list entry '@bad.org'` / `not covered by any allow list entry`
  - IP filter: `matches IP blacklist entry '203.0.113.0/24'` / `not covered by any IP whitelist entry`
  - Dynamic IP blocking (MAIL FROM, AUTH, DATA): block expiry is included (`until 14:32:05 UTC`)
- Failed SMTP AUTH logs the reason (`unknown user`, `user is disabled`, `wrong password`,
  `no usable password configured`) — log only; the SMTP response stays generic
- MAIL FROM after failed auth names the username that failed
- Admin-notification skip names the missing setting (SenderAddress vs RecipientAddresses)

#### Large attachments require Mail.ReadWrite
- Messages with attachments ≥ 3 MB failed with `HTTP 403 ErrorAccessDenied`: Graph's
  `sendMail` request is capped at 4 MB, so large attachments are delivered via a
  draft + upload session — mailbox **write** operations that `Mail.Send` alone does
  not permit. There is no Graph alternative below `Mail.ReadWrite` for this path
  (sendMail with raw MIME is subject to the same 4 MB write-request limit)
- The Entra setup wizard now grants **Mail.ReadWrite** (application) alongside
  Mail.Send and User.Read.All. Existing app registrations: re-run the wizard
  (it grants only what is missing) or add the permission manually in Entra
- **Security recommendation**: scope the app to the allowed sender mailboxes with an
  Exchange Online Application Access Policy (`New-ApplicationAccessPolicy` against a
  mail-enabled security group) — without it, Mail.Send and Mail.ReadWrite apply to
  every mailbox in the tenant

#### Retry policy: outage-proof defaults + capped backoff
- The previous defaults (3 attempts, 60 s base, uncapped doubling) gave up
  **3 minutes** after the first attempt — any short internet outage permanently
  failed messages
- The exponential backoff is now **capped at 30 minutes** so retries keep coming
  at a sane rate during long outages instead of doubling into multi-hour gaps
- New default `MaxRetries = 10` (UI range raised to 1–20): attempts at
  0, 1, 3, 7, 15, 31, 61, 91, 121, 151 min ≈ **2.5 h delivery window**
  (20 attempts ≈ 7.5 h) — an outage well over 30 minutes no longer loses mail
- Messages page: the Attempts column now counts **tries used** — for delivered
  messages the successful attempt is included (first-try delivery shows "1/10"
  instead of the confusing "0/10"); queued/failed entries keep counting failed
  attempts so far

#### Graph API health check: real probe instead of fake mail
- The connectivity monitor "probed" Graph by sending a notification as the fake
  user `healthcheck@invalid.local` every check interval. This logged a warning
  with a full ODataError stack trace every 15 minutes — and was functionally
  dead: `SendNotificationAsync` swallows all exceptions, so the monitor could
  **never** detect an outage and always reported "reachable"
- The check now acquires a real OAuth2 token (`GraphConnectivityProbe`) with a
  fresh credential per probe (a reused credential would answer from MSAL's token
  cache and mask outages). Validates certificate/secret, app registration and
  network without sending anything — no more log noise
- Down/restored notifications now actually fire; covered by 4 new unit tests and
  a live token-acquisition test
- The check also **verifies the granted application permissions**: the probe reads
  the token's `roles` claim (no extra Graph call) and alerts when `Mail.Send`,
  `Mail.ReadWrite` or — with sender validation enabled — `User.Read.All` is
  missing. Each distinct gap is reported once (log error + admin notification)
  with a hint to re-run the Entra setup wizard

#### Port health probes no longer pollute the log
- Every PortMonitor check logged "Connection accepted" / "Session completed" at
  Information (and on TLS ports a session fault) — recurring noise per interval
- The port monitor now announces its probes via an in-process `PortProbeRegistry`;
  the SMTP session logging recognizes loopback connections within the probe window
  and logs them at Debug instead. Real client connections are unaffected
  (non-loopback traffic is never demoted)

#### Log viewer fixes
- The "Auto-scroll" checkbox had no effect on the periodic reload: the list was
  replaced every 5 s regardless, so entries moved while reading or searching.
  The checkbox is now **Auto-refresh**: enabled = live tail (reload + jump to newest);
  disabled = the view is frozen (the Refresh button still works)
- The level filter matched the selected level **exactly** (choosing Debug hid
  Information/Warning/Error). It is now a minimum-severity filter: "Debug+" shows
  the selected level and everything above it
- The selected log entry (and its details panel) survives an auto-refresh

#### Release build: framework-dependent by default
- The release publish no longer bundles the .NET runtime: **89 files / ~118 MB**
  instead of 323 files / ~260 MB. The target machine must have the
  **.NET Desktop Runtime 8 (x64)** installed (covers both apps; the service only
  needs the base runtime included in it)
- `build-release.ps1 -SelfContained` still produces the fully bundled package
  for machines where installing .NET is not an option
- Missing-runtime handling: without any .NET, the native app host itself shows a
  download dialog (ConfigTool) or console message (`GraphMailer.exe --install`)
  before managed code runs. The ConfigTool additionally verifies the service
  runtime (`DotNetRuntimeCheck`) before installing the service and points at the
  Desktop-Runtime download when a service start fails without a runtime —
  this catches mixed deployments (e.g. self-contained ConfigTool next to a
  framework-dependent service)
- The service logs the active runtime version at startup

#### Release output trimmed
- `SatelliteResourceLanguages=en`: the self-contained publish no longer includes
  the 13 localized .NET/WPF resource folders (~16 MB, 220+ files) — the app is
  English-only and the English fallback always works
- `appsettings.Development.json` is excluded from publish (dev-only)
- Release folder: 323 files / ~260 MB, everything flat in one directory

### Fixed

- **ConfigTool: misleading IP filter hint texts corrected** — the whitelist hint
  claimed whitelisted IPs "bypass authentication and sender filtering" (they don't;
  a non-empty whitelist is an exclusive allow-list and all other checks still apply),
  and the blacklist hint claimed rejection happens "before the SMTP banner is sent"
  (it happens at MAIL FROM). Both texts on the IP Filtering page and in the
  add/edit dialogs now describe the actual behavior

### Fixed (code review follow-up)

- **Log level does not require a restart** — `graphmailer.json` is loaded with
  `reloadOnChange: true` and Serilog.Settings.Configuration hot-swaps the minimum level;
  the log level was removed from the restart-badge fingerprint (the 2026-06-10 notes
  below claiming otherwise were corrected)
- Restart badge logic redesigned: the badge now reflects "config on disk differs from
  config the running service loaded". This fixes three bugs: a failed Save no longer
  suppresses the badge on retry, reverting a change and saving again clears the badge
  without a restart, and a transient `sc.exe` polling error no longer clears it falsely
- Restart-snapshot fingerprint is now JSON-serialized instead of string-concatenated
  (free-text fields like the SMTP banner could previously collide with the delimiters)
- `ConfigService.ReadLogging` now understands Serilog's string shorthand
  (`"MinimumLevel": "Debug"`); previously it was read as Information and overwritten on save
- `ConfigService.WriteLogging` no longer materializes a `MinimumLevel` section for the
  default value — it would have permanently shadowed a level configured in appsettings.json
- `Fatal` added to the log-level ComboBox; the level list is now single-sourced from
  the `LogLevels` array (a hand-configured `Fatal` was previously rewritten to `Information`)
- Service status polling (`sc.exe queryex`) moved off the UI thread (`Task.Run`);
  a wedged SCM no longer freezes the ConfigTool
- `MetricsPage` no longer runs its SQLite queries in the constructor at app startup;
  data loads when the page is first shown (as before via `IsVisibleChanged`)

## 2026-06-10

### Added

#### Non-Delivery Reports (NDR)
- New `NdrOptions` configuration class (`NdrNotifications` section in `graphmailer.json`)
  - `Enabled` — activates NDR sending globally
  - `NotifySender` — sends an NDR to the original sender (loop-safe: skipped if sender equals the configured Graph sender address)
  - `NotifyAdmin` — sends a copy of the NDR to admin recipients
- `IAdminNotificationService.SendNdrAsync` — new method for routing NDRs; fire-and-forget, never enters the mail queue
- `AdminNotificationService.SendNdrAsync` — implementation including NDR body with From, To, Subject, Message-ID, Sent timestamp and delivery error
- `QueueProcessor` calls `SendNdrAsync` after a message permanently fails (after all retries exhausted)
- NDR section wired into `ConfigService` (read/write round-trip for `graphmailer.json`)
- NDR card on the **Notifications** page in ConfigTool with three toggle switches
- Service registration: `NdrOptions` bound via `IOptionsMonitor<NdrOptions>` in `Program.cs`
- 7 new unit tests in `AdminNotificationServiceNdrTests`:
  `SendNdrAsync_Disabled_DoesNotSend`, `SendNdrAsync_NotifySender_SendsToOriginalSender`,
  `SendNdrAsync_NotifyAdmin_SendsToAdminRecipients`, `SendNdrAsync_BothEnabled_SendsTwice`,
  `SendNdrAsync_EmptyFrom_SkipsSenderNdr`, `SendNdrAsync_FromEqualsAdminSender_SkipsSenderNdrToPreventLoop`,
  `SendNdrAsync_NoSenderAddress_DoesNotSend`

#### Log Level Configuration
- New `LoggingSection` in `ConfigDocument` (`DefaultLevel`, default `"Information"`)
- `ConfigService` reads and writes `Serilog → MinimumLevel → Default` in `graphmailer.json`
- **Logging card** added to the **Metrics** page in ConfigTool: ComboBox with levels Verbose / Debug / Information / Warning / Error
- `MetricsPage` now participates in the full config load/save/discard cycle (`LoadFrom` / `CollectTo`)

#### "Restart Required" Badge
- Toolbar badge "⚠ Restart required" (orange) appears after saving settings that cannot be applied without a service restart
- Settings that trigger the badge: SMTP listeners (port, mode, auth), SMTP banner, max message size, TLS certificate, mail queue directory, polling interval (log level was initially included but removed on 2026-06-11 — it hot-reloads)
- Badge clears automatically when a service restart is detected via two mechanisms:
  - **Slow restart** (> 5 s): detected when the service transitions STOPPED → RUNNING in the status poller
  - **Fast restart** (< 5 s, e.g. Restart button): detected by PID change while service remains in RUNNING state
- Status poller upgraded from `sc query` to `sc queryex` to obtain the service PID for restart detection

### Fixed

#### Certificate Store Tests (3 previously failing)
- `LoadCertificate_SubjectMatch_ReturnsCert`, `LoadCertificate_SubjectAndIssuerMatch_ReturnsCert`,
  `LoadCertificate_MultipleMatches_ReturnsLatestExpiry` were failing because `RSA.Create(2048)`
  produces an ephemeral CNG key with no Windows KSP backing file; after `store.Add()` + `store.Close()`,
  the cert was re-read with `HasPrivateKey = false`, causing `CertificateStoreService.LoadCertificate()`
  to filter it out
- Fixed by exporting the ephemeral cert to PFX and reimporting with `PersistKeySet | Exportable`,
  which writes the private key to a real KSP container
- Added `DeletePersistedKey()` cleanup helper (calls `RSACng.Key.Delete()`) called in each test's
  `finally` block to remove the KSP container after the test
- Bonus: `IssuerMismatch` and `SubjectNotFound` tests now pass for the correct reason

### Changed

#### ConfigTool — Scrollable Lists
- All DataGrids that accept user-managed entries now have `MaxHeight="240"` so they scroll
  internally instead of growing the page. Affected grids:
  - **IP Filtering**: `WhitelistGrid`, `BlacklistGrid`, `BlockedGrid`
  - **Access Control**: `UsersGrid`, `SendersGrid`, `BlockedSendersGrid`, `RecipientsGrid`, `BlockedRecipientsGrid`
  - **Notifications**: `RecipientsGrid`
  - **Servers & TLS**: `ListenersGrid`

#### ConfigTool — MetricsPage lifecycle
- `MetricsPage` is now created eagerly at startup (alongside all other config pages) so that
  `CollectTo` always runs on Save and `LoadFrom` always runs on Discard, even if the page was
  never navigated to

#### Tests
- `AdminNotificationServiceTests.CreateService` updated to pass an `IOptionsMonitor<NdrOptions>`
  (with `Enabled = false`) as the new third constructor argument
- `TEST_DOCUMENTATION.md` updated: total 338 tests (300 unit · 38 integration),
  new section "AdminNotificationService — NDR" with 7 rows

---

### Notes on live reload vs. restart

Settings marked with ⚠ in the ConfigTool require a service restart because they are consumed
at process startup and cannot be hot-reloaded:

| Setting | Reason |
|---|---|
| SMTP listeners (port, mode, auth, name) | `SmtpRelayService` starts listeners once on startup |
| SMTP banner, max message size | Read once in `BuildServer()` at startup |
| TLS certificate (store, subject, issuer) | Bound via `IOptions<>` (not Monitor); loaded once |
| Mail queue directory | Stored as field in constructor of `QueueProcessor` and `MailQueueWriter` |
| Polling interval | Used to create `PeriodicTimer` at startup |

All other settings (Graph API credentials, access lists, IP filtering, notifications, NDR,
monitoring thresholds, metrics, mail queue retries/batch/archive) are picked up live via
`IOptionsMonitor<T>` without restarting the service. The log level also reloads live:
graphmailer.json is registered with `reloadOnChange: true` and Serilog.Settings.Configuration
subscribes to configuration change tokens.
