# Mail Queue

This is the **Mail Queue** page of the Configuration Tool. It controls how GraphMailer stores
accepted mail, how often it tries to deliver it, how long it keeps retrying before giving up, and
whether successfully sent mail is archived.

Every message GraphMailer accepts is written to disk first, then delivered from there — so nothing
is lost if delivery is briefly impossible (a Microsoft 365 or network outage).

> [!IMPORTANT]
> The **Polling interval** and the **Mail base directory** take effect only after a service
> **restart** (a *“Restart required”* badge appears). The retry settings, archiving and retention
> apply **without** a restart.

## Retry & Polling

GraphMailer uses a two-phase, time-based retry policy modelled on Microsoft Exchange: a message is
retried quickly a few times (for transient blips), then at a steady interval, until its expiration
time elapses — measured from when it was **received**.

| Setting | Default | Range | Meaning |
|---|---|---|---|
| Polling interval (seconds) | `5` | 1–1440 | How often the queue is scanned for new messages. Lower = faster pickup, more CPU. |
| Transient retries (count) | `6` | 0–20 | Fast initial retries before switching to the steady interval. |
| Transient interval (seconds) | `300` | 10–3600 | Wait between the fast retries (5 min). |
| Steady interval (seconds) | `900` | 30–86400 | Retry interval after the transient phase (15 min). |
| Message expiration (hours) | `24` | 0–240 | How long a message is retried before it is given up. `0` = fail on the first error. |

The page shows a **Calculated retry schedule** that updates live as you change these values — it is
exactly the schedule the service will apply.

> [!NOTE]
> Give-up is by **time, not attempt count**. A long Microsoft 365 or internet outage that stays
> within the expiration window never permanently fails mail — delivery resumes when the connection
> returns. Only when the expiration time is exceeded is the message moved to *failed* and (if
> enabled) a non-delivery report is sent.

## Mail Directories

All mail lives under one **Mail base directory** (default
`C:\ProgramData\GraphMailer\mail`). You can point it at another path or drive with the **…** browse
button, or reset it to the default with **✕**. Three sub-folders are derived from it (shown
read-only):

| Folder | Contents |
|---|---|
| `queue\` | Accepted messages awaiting delivery. |
| `sent\` | Successfully delivered messages (only when archiving is enabled). |
| `failed\` | Messages that exhausted the retry window, kept for manual inspection until the failed-mail retention period elapses. |

Each message is stored as an `{id}.eml` file plus an `{id}.meta.json` sidecar. You can browse these
folders from the [Messages](../monitoring/messages.html) page.

> [!TIP]
> Moving the mail directory to a dedicated drive can help on a busy relay. Remember it requires a
> restart, and the service account (LocalSystem) must be able to write to the new location.

## Archiving & Cleanup

| Setting | Default | Range | Meaning |
|---|---|---|---|
| Enable archiving of sent mail | Off | — | When on, delivered messages are copied to `sent\` instead of being discarded. |
| Retention (days) | `7` | 1–9125 | How long archived mail is kept before the hourly cleanup deletes it. |
| Failed-mail retention (days) | `60` | 0–9125 | How long permanently failed messages are kept in `failed\` for diagnosis before the hourly cleanup deletes them. `0` keeps them forever. |

> [!WARNING]
> Archiving keeps a full copy of every delivered message on disk — including its content and
> attachments. Make sure the retention period and disk capacity match your storage and data-
> retention policy, and that the location is appropriately secured.

## Related

- [Messages](../monitoring/messages.html) — browse the queue, sent and failed folders
- [Notifications](notifications.html) — non-delivery reports and failed-queue alerts
- [Health Checks](health-checks.html) — disk-space monitoring for the mail directory
