# Notifications

This is the **Notifications** page of the Configuration Tool. It controls the emails GraphMailer
sends *to you*: operational alerts when something needs attention, optional non-delivery reports
(bounces) for failed mail, and a scheduled HTML operations report.

All of these are sent **through Microsoft 365 using the same Graph connection** as normal mail, so
the [Graph API](graph-api.md) connection must be working first. All system emails share one
styled HTML design (matching the operations report); NDRs additionally carry a plain-text
fallback for legacy applications that parse bounce messages.

> [!NOTE]
> Changes on this page apply to the running service **without a restart**.

## Notification Settings

The shared basics: every email GraphMailer sends about itself goes out from this address. It is
used by all the features below **and** by [emailed backups](backup-restore.md), which is why it
comes first.

| Setting | Default | Meaning |
|---|---|---|
| Sender email address | — | The **From** address for every notification email. Must be a mailbox the Graph service principal may send as (`Mail.Send`). |
| Subject prefix | `[GraphMailer]` | Prepended to every notification subject — handy for inbox rules and filtering. |

> [!IMPORTANT]
> Set the sender address to a real mailbox in your tenant (the same kind of address you use for the
> [test email](graph-api.md)). There is **no fallback account**: without a sender, nothing that
> sends email can work — admin notifications, non-delivery reports, scheduled reports **and**
> [emailed backups](backup-restore.md). The ConfigTool therefore shows a validation error under
> the field and refuses to save while any of these features is active without a (valid) sender.
>
> The error names exactly which features still depend on it. Switch those off — including the
> **Send admin notifications** master switch below — and the address becomes optional again.

## Non-Delivery Reports (NDR)

When a message is accepted over SMTP but **permanently** rejected by Microsoft 365 (after the retry
window expires), GraphMailer can send a bounce notification. NDRs have their own master switch and
are **not** affected by the admin-notification one below.

| Setting | Default | Meaning |
|---|---|---|
| Enable Non-Delivery Reports | Off | Master switch for bounces. |
| Send NDR to original sender | On | Notifies the address that submitted the message. |
| Send NDR copy to admin recipients | Off | Also sends a copy to the [Admin Recipients](#admin-recipients). |

> [!TIP]
> Enabling NDRs to the original sender makes GraphMailer behave like a normal mail server — the
> submitting application/user learns their message bounced instead of failing silently.

> [!NOTE]
> **Send NDR copy to admin recipients** stays switched off and locked until at least one admin
> recipient exists — a copy needs somewhere to go. The Configuration Tool says so next to the
> switch; add a recipient below and it unlocks.

## Admin Recipients

The email addresses that receive **all** system notification emails (alerts, NDR admin copies, the
periodic report). Add one or more with **+ Add**. With no recipients configured, GraphMailer has
nowhere to send alerts.

## Admin Notifications

| Setting | Default | Meaning |
|---|---|---|
| Send admin notifications | Off | Master switch for every alert in *Notify on Events* below. |

Turning this off stops all admin alerts at once without losing anything: the recipient list, the
sender address and every individual event setting are kept and take effect again when you switch it
back on. While it is off, the event toggles are greyed out.

> [!TIP]
> Use this to silence alerts during a planned maintenance window, or to clear the sender address:
> with the master switch off, the sender is no longer required by admin notifications, so it can be
> emptied without first deleting the recipients.

Non-delivery reports and the periodic operations report have their own switches and are **not**
covered by this master switch — they can still send while it is off. For the same reason the
recipient list stays editable: those two features use it as well.

## Notify on Events

Toggle which conditions raise an alert email. Sensible defaults are pre-set:

| Event | Default |
|---|---|
| IP address blocked | On |
| Email delivery failed (all retries exhausted) | On |
| TLS listener certificate expiring within warning threshold | On |
| TLS listener certificate expired | On |
| Graph client certificate expiring (Entra authentication) | On |
| Low disk space | On |
| Graph API unreachable | On |
| SMTP port connectivity failure | On |
| Service started / stopped | **Off** |
| Configuration backup result (success / failure) | On |
| New GraphMailer version available | **Off** |
| Malware detected in an incoming message | On |
| Malware scans are failing | On |

The two malware alerts relate to the [Malware Scan](malware-scan.md) page:

- **Malware detected** is batched — a malware wave would otherwise produce one email per message.
  The window is **60 seconds measured from the first detection**, not from the last, so a steady
  trickle of detections cannot keep postponing the alert. It also reports **audit-mode** detections,
  which were delivered rather than blocked; the wording says which.
- **Malware scans are failing** fires after repeated scan errors or timeouts (5 within 5 minutes by
  default). It exists because scanning **fails open**: when the scanner cannot answer, the message is
  delivered unscanned. Without this alert, mail flowing past a broken scanner is invisible outside
  the log.

The **All** switch in the card header turns every event on or off at once. It shows as on only
while every single event is enabled, so it also tells you at a glance whether anything is switched
off.

The thresholds behind several of these (certificate warning days, disk-space percentage, port
outage threshold, check intervals) are set on the [Monitoring](monitoring.md) page. *How often* an
alert repeats is set under [Repeat & Recovery](#repeat--recovery) below.

### The two certificates are not the same thing

GraphMailer watches **two independent certificates**, and the difference decides what can still be
reported when one of them lapses:

| Certificate | Configured on | What it does | Alerts |
|---|---|---|---|
| TLS listener | [Servers & TLS](servers-tls.md) | Secures the SMTP ports | *expiring* **and** *expired* |
| Graph client | [Graph API](graph-api.md) | Authenticates against Entra (certificate auth only) | *expiring* only |

If the **TLS listener certificate** expires, Graph still works, so both alerts go out normally.

If the **Graph client certificate** expires, GraphMailer can no longer obtain a Graph token — mail
delivery stops completely and no email can be sent, not even to report the problem. There is
therefore deliberately no "Graph client certificate expired" alert: it could never be delivered.

> [!IMPORTANT]
> The *Graph client certificate expiring* warning is the **last message you will get** before
> delivery stops. It is on by default and worth leaving on. Once the certificate has actually
> lapsed, the condition appears only in the log and as a **Graph Certificate** row in the health
> checks (Status page and the periodic report) — so on a certificate-authenticated installation it
> is worth tracking that expiry date outside GraphMailer as well.

The warning uses the same threshold as the TLS certificate (**certificate warning days** on the
[Monitoring](monitoring.md) page) and only applies when Graph API uses certificate authentication;
with a client secret the alert never fires.

> [!NOTE]
> The *New GraphMailer version available* alert additionally requires the weekly **update check**
> to be enabled on the [Monitoring](monitoring.md) page. You receive **one email per new
> release**, not a weekly reminder.

## Repeat & Recovery

Some problems are a **lasting condition** rather than a one-off event: the disk stays full, the
certificate stays expired, the port stays unreachable. These two settings decide what happens while
such a condition persists, and they apply to **every** monitor identically — one cadence, no
per-monitor surprises.

| Setting | Default | Range | Meaning |
|---|---|---|---|
| Repeat after (minutes) | `1440` (daily) | 0–10080 | How long a condition that is still present stays quiet before you are reminded. `0` reports it exactly once and stays silent until it clears. |
| All-clear notifications | On | — | Send a follow-up email when a reported problem is resolved. |

Covered by these settings:

- Low disk space
- TLS listener certificate expiring / expired
- Graph client certificate expiring
- SMTP port unreachable
- Graph API unreachable
- Missing Graph application permissions

**Not** covered — these report a moment in time, not a state, so repeating them makes no sense:
configuration backup result, new version available, service started/stopped, and failed mail
delivery (which is batched into one summary email instead).

Three details worth knowing:

- A condition that **changes** is reported straight away rather than waiting out the interval. A
  certificate going from *expiring* to *expired*, or a second Graph permission being revoked, is a
  new situation, not a repeat.
- An **all-clear is only ever sent for something that was actually reported**. Switching an alert
  off cannot produce a stray "resolved" email, and a brief blip that never crossed the port outage
  threshold produces neither an alert nor an all-clear.
- The state survives a **service restart**. Restarting during an outage does not re-alert, and an
  outage that started before the restart still produces its all-clear when it ends.

> [!TIP]
> The default reminds you once a day while something is broken. If your monitoring is elsewhere and
> you only want to be told once per incident, set **Repeat after** to `0`.

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

The report closes with a short **Recommendations** box listing the suggestions that currently apply
to this installation — the same list shown on the
[Recommendations](../monitoring/recommendations.md) page in the Configuration Tool, grouped by
category and naming the page that fixes each one. It is a hint, not a warning: it never affects the
report's health status, it disappears once nothing applies, and tips you hide in the Configuration
Tool are left out of the email too.

## Related

- [Recommendations](../monitoring/recommendations.md) — the full list of suggestions and how to hide them
- [Graph API](graph-api.md) — the connection used to send notifications
- [Monitoring](monitoring.md) — the thresholds that trigger many of these alerts
- [Mail Queue](mail-queue.md) — when a message becomes a permanent failure (NDR)
