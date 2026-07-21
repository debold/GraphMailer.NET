# Monitoring

This is the **Monitoring** page of the Configuration Tool. It tunes the background self-checks
GraphMailer runs to catch problems early — expiring certificates, low disk space, an unreachable
SMTP port, or a broken Microsoft 365 connection — and it controls how statistics and logs are
recorded.

When a check fails, GraphMailer raises an admin notification (if configured on the
[Notifications](notifications.html) page) and reflects the condition in the scheduled report and on
the [Status](../monitoring/status.html) page.

> [!NOTE]
> Changes on this page apply to the running service **without a restart**. The log level in
> particular takes effect immediately.

## Update Check

Once a week, queries the GraphMailer releases on GitHub (`api.github.com`) and reports whether a
newer version is available. The result appears in the **Software Update** card on the
[Status](../monitoring/status.html) page, which also offers a **Check now** button for an immediate
check, and as a **Software Update** row in the health table of the scheduled
[report](notifications.html). An optional admin email for new releases can be enabled on the
[Notifications](notifications.html) page (one email per new version).

| Setting | Default | Meaning |
|---|---|---|
| Check for updates | Off | Master switch for the weekly release check. While disabled, no request leaves the machine. |

> [!NOTE]
> The check only downloads the public release information from GitHub — it sends no data about
> your installation, and it never installs anything automatically. A failed check (e.g. no
> internet access) is retried the next day.

## Anonymous Usage Telemetry

Once a day, sends **one anonymous report** to the GraphMailer developer to help improve the
software: which versions are in use, whether mail flows succeed, and which unexpected errors occur
in the field.

| Setting | Default | Meaning |
|---|---|---|
| Send telemetry | Off | Master switch. While disabled, nothing is collected and nothing leaves the machine. |
| Last transmission | — | Read-only: this installation's random id and when the last report was sent. |

**Exactly what is transmitted** (and nothing else):

- **Heartbeat (daily)** — a random installation id (a GUID, not derived from your hardware, user,
  or network), the GraphMailer version, Windows and .NET runtime version, service uptime, the
  *number* of received / sent / failed mails since the last report, and the configuration *shape*
  (how many listeners, whether TLS/authentication/archiving are enabled). Also aggregated
  mail-traffic *counters* from the statistics database: SMTP sessions
  (total / aborted / faulted / TLS / authenticated), rejection counts grouped by cause (IP /
  auth / sender / recipient / size), mails with attachments, deliveries on the first try /
  after retries / via upload session, and the average queue latency — counts and averages
  only, never IP addresses, mail addresses or usernames.
- **Error reports** — for unexpected errors only (log level Error or Fatal): the exception type,
  the stack trace, the log message *template* (e.g. `Delivery failed for {MessageId}` — the
  placeholders, never the actual values), the affected component, the Microsoft Graph error code /
  HTTP status if any, and how often the error occurred.

**Never transmitted**: email addresses, message content, subjects, IP addresses, hostnames, user
or tenant names, or any configuration values. Error messages are deliberately dropped because they
could contain such data — only type names and code locations are kept.

> [!NOTE]
> The data is sent to the developer's Microsoft Azure telemetry endpoint (Application Insights,
> EU region) over HTTPS. A failed transmission is retried hourly and never affects mail delivery.

## Certificate Monitoring

Warns before a certificate (the TLS listener certificate or the Entra authentication certificate)
expires, so you can renew in time.

| Setting | Default | Range | Meaning |
|---|---|---|---|
| Warning threshold (days) | `14` | 1–60 | Raise a notification when a certificate expires within this many days. |

## Disk Space Monitoring

Warns when the drive holding GraphMailer's data is running low — important because accepted mail is
queued to disk.

| Setting | Default | Range | Meaning |
|---|---|---|---|
| Warning threshold (%) | `10` | 1–100 | Notify when free disk space falls below this percentage. |

## Port Connectivity Checks

Periodically opens a TCP connection to each configured SMTP listener port to confirm it is actually
accepting connections.

| Setting | Default | Range | Meaning |
|---|---|---|---|
| Check interval (minutes) | `5` | 1–1440 | How often each listener port is probed. A failed probe raises an alert. |

## Graph API Monitoring

Periodically authenticates against the Graph API to confirm the credentials still work and the
required permissions are present.

| Setting | Default | Range | Meaning |
|---|---|---|---|
| Check interval (minutes) | `15` | 1–1440 | How often the Graph connection is verified. A failure raises an alert. |

> [!TIP]
> This check is what warns you when admin consent was revoked, a client secret expired, or a
> permission is missing — long before users notice mail failing. Keep it enabled.

## Metrics Storage

Records email and performance statistics to the local SQLite database that feeds the
[Metrics](../monitoring/metrics.html) page and the scheduled report.

| Setting | Default | Range | Meaning |
|---|---|---|---|
| Enable metrics | On | — | Master switch for statistics recording. |
| Retention (days) | `90` | 1–3650 | How long records are kept before automatic cleanup. |
| Cleanup interval (hours) | `24` | 1–168 | How often expired records are removed. |

## Performance Metrics

Periodically samples the service's resource usage (memory, CPU, disk) into the metrics database.

| Setting | Default | Range | Meaning |
|---|---|---|---|
| Enable performance metrics | On | — | Master switch for resource sampling. |
| Memory sample interval (s) | `60` | 10–3600 | How often working-set memory is recorded. |
| CPU sample interval (s) | `60` | 10–3600 | How often CPU usage is recorded. |
| Disk sample interval (s) | `300` | 10–86400 | How often disk usage is recorded. |

## Logging

| Setting | Default | Meaning |
|---|---|---|
| Log level | `Information` | Minimum level written to the log files. Lower it to `Debug` for detailed per-request troubleshooting; raise it to `Warning` to log only problems. |

> [!NOTE]
> The log level changes **immediately**, no restart needed. Microsoft and System framework logs are
> always kept at `Warning` regardless of this setting, to keep the logs readable. View the files on
> the [Logs](../monitoring/logs.html) page.

## Related

- [Notifications](notifications.html) — where alerts from these checks are delivered
- [Metrics](../monitoring/metrics.html) — the statistics this page records
- [Logs](../monitoring/logs.html) — the log files the log level controls
