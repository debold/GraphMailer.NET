# Notifications

This is the **Notifications** page of the Configuration Tool. It controls the emails GraphMailer
sends *to you*: operational alerts when something needs attention, optional non-delivery reports
(bounces) for failed mail, and a scheduled HTML operations report.

All of these are sent **through Microsoft 365 using the same Graph connection** as normal mail, so
the [Graph API](graph-api.html) connection must be working first. All system emails share one
styled HTML design (matching the operations report); NDRs additionally carry a plain-text
fallback for legacy applications that parse bounce messages.

> [!NOTE]
> Changes on this page apply to the running service **without a restart**.

## Admin Recipients

The email addresses that receive **all** system notification emails (alerts, NDR admin copies, the
periodic report). Add one or more with **+ Add**. With no recipients configured, GraphMailer has
nowhere to send alerts.

## Notification Settings

| Setting | Default | Meaning |
|---|---|---|
| Sender email address | — | The **From** address for every notification email. Must be a mailbox the Graph service principal may send as (`Mail.Send`). |
| Subject prefix | `[GraphMailer]` | Prepended to every notification subject — handy for inbox rules and filtering. |

> [!IMPORTANT]
> Set the sender address to a real mailbox in your tenant (the same kind of address you use for the
> [test email](graph-api.html)). There is **no fallback account**: without a sender, nothing that
> sends email can work — admin notifications, non-delivery reports, scheduled reports **and**
> [emailed backups](backup-restore.html). The ConfigTool therefore shows a validation error under
> the field and refuses to save while any of these features is active without a (valid) sender.

## Non-Delivery Reports (NDR)

When a message is accepted over SMTP but **permanently** rejected by Microsoft 365 (after the retry
window expires), GraphMailer can send a bounce notification.

| Setting | Default | Meaning |
|---|---|---|
| Enable Non-Delivery Reports | Off | Master switch for bounces. |
| Send NDR to original sender | On | Notifies the address that submitted the message. |
| Send NDR copy to admin recipients | Off | Also sends a copy to the Admin Recipients above. |

> [!TIP]
> Enabling NDRs to the original sender makes GraphMailer behave like a normal mail server — the
> submitting application/user learns their message bounced instead of failing silently.

## Notify on Events

Toggle which conditions raise an alert email. Sensible defaults are pre-set:

| Event | Default |
|---|---|
| IP address blocked | On |
| Email delivery failed (all retries exhausted) | On |
| Certificate expiring within warning threshold | On |
| Certificate expired | On |
| Low disk space | On |
| Graph API unreachable | On |
| SMTP port connectivity failure | On |
| Service started / stopped | **Off** |
| Configuration backup result (success / failure) | On |
| New GraphMailer version available | **Off** |

The thresholds behind several of these (certificate warning days, disk-space percentage, port and
Graph check intervals) are set on the [Monitoring](monitoring.html) page.

> [!NOTE]
> The *New GraphMailer version available* alert additionally requires the weekly **update check**
> to be enabled on the [Monitoring](monitoring.html) page. You receive **one email per new
> release**, not a weekly reminder.

## Periodic Reports

Send an HTML **operations report** — health checks, statistics, mail-queue status and recent
failures — to the Admin Recipients on a schedule.

| Setting | Default | Notes |
|---|---|---|
| Enable periodic reports | Off | Master switch. |
| Frequency | Weekly | Weekly or Monthly. |
| Day of week | Monday | Used for Weekly. |
| Day of month | `1` | Used for Monthly (1–28, so it exists in every month). |
| Time of day | `07:00` | 24-hour `HH:mm`, in the **service's local time**. |

> [!NOTE]
> The report goes to the same **Admin Recipients** — there is no separate recipient list. It is the
> same report design used throughout GraphMailer's emails, sent automatically on your chosen
> schedule.

## Related

- [Graph API](graph-api.html) — the connection used to send notifications
- [Monitoring](monitoring.html) — the thresholds that trigger many of these alerts
- [Mail Queue](mail-queue.html) — when a message becomes a permanent failure (NDR)
