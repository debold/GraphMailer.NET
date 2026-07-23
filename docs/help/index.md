# GraphMailer Help

GraphMailer is a Windows SMTP relay that accepts mail from legacy applications and devices on
your network and delivers it through Microsoft 365 using the Graph API — no SMTP AUTH against
Exchange Online, no basic authentication, no on-premises mail server required.

This help covers installation, first-time setup, and a reference page for every screen in the
**GraphMailer Configuration Tool**.

> [!TIP]
> Every page in the Configuration Tool has a help icon in the toolbar (and the **F1** key) that
> opens the matching page here — so you can jump straight from a setting to its documentation.

## New here? Start with these

1. **[Installation](getting-started/installation.md)** — install the service and the ConfigTool.
2. **[Quickstart](getting-started/quickstart.md)** — what to do right after installation.
3. **[Entra / Graph Setup](getting-started/entra-setup.md)** — register the app in Microsoft 365 so GraphMailer may send mail.

## Configuration reference

One page per screen in the ConfigTool, in the same order as the app's sidebar:

- [Servers & TLS](configuration/servers-tls.md) — SMTP listeners, ports, encryption, banner, size limits
- [Access Control](configuration/access-control.md) — SMTP users and authentication
- [IP Filtering](configuration/ip-filtering.md) — allowed networks and automatic blocking
- [Graph API](configuration/graph-api.md) — Microsoft 365 connection and certificate
- [Mail Queue](configuration/mail-queue.md) — delivery, retries, archiving
- [Monitoring](configuration/monitoring.md) — self-check thresholds, metrics recording, log level
- [Notifications](configuration/notifications.md) — admin alerts and the scheduled report
- [Backup & Restore](configuration/backup-restore.md) — configuration backups

## Monitoring

- [Status](monitoring/status.md) · [Recommendations](monitoring/recommendations.md) · [Metrics](monitoring/metrics.md) · [Messages](monitoring/messages.md) · [Logs](monitoring/logs.md)

## Reference

- [Troubleshooting](reference/troubleshooting.md) · [FAQ](reference/faq.md) · [Glossary](reference/glossary.md)
