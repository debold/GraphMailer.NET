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

### Throttled sender mailboxes

Exchange Online limits how many requests a single mailbox handles at once. When a mailbox is
overloaded — typically because other clients or apps work on it at the same time — Microsoft 365
answers with a throttling error (`ErrorDirectoryConcurrencyLimit`, `CommandConcurrencyLimitReached`,
HTTP 429/503/504). GraphMailer then **holds back every queued message from that mailbox** until the
failed message's next retry, instead of trying each one in turn against a mailbox that is already
saturated. Mail from all other senders keeps flowing.

Held messages are not counted as failed attempts and keep their full expiration window. When the
hold ends, the oldest message goes out first; once it is delivered, the rest follow. The log shows
one warning when a mailbox is first held (*“Mailbox … is throttled by Exchange Online”*) and an
information line when it accepts mail again. See
[Troubleshooting](../reference/troubleshooting.md#one-sender-is-delayed-mailbox-throttled) if a
mailbox stays throttled.

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
folders from the [Messages](../monitoring/messages.md) page.

> [!TIP]
> Moving the mail directory to a dedicated drive can help on a busy relay. Remember it requires a
> restart, and the service account (LocalSystem) must be able to write to the new location.

## Archiving & Cleanup

| Setting | Default | Range | Meaning |
|---|---|---|---|
| Enable archiving of sent mail | Off | — | When on, delivered messages are copied to `sent\` instead of being discarded. |
| Retention (days) | `7` | 1–9125 | How long archived mail is kept before the [hourly cleanup](#when-the-cleanup-runs) deletes it. |
| Failed-mail retention (days) | `60` | 0–9125 | How long permanently failed messages are kept in `failed\` for diagnosis before the [hourly cleanup](#when-the-cleanup-runs) deletes them. `0` keeps them forever. |

> [!WARNING]
> Archiving keeps a full copy of every delivered message on disk — including its content and
> attachments. Make sure the retention period and disk capacity match your storage and data-
> retention policy, and that the location is appropriately secured.

### When the cleanup runs

The service deletes expired mail itself. There is no Windows scheduled task to set up.

- **Schedule:** once when the service **starts**, then **every hour**. The hour is counted from the
  service start, not from a fixed time of day. A restart runs the cleanup immediately and starts a
  new hourly cycle. While the service is stopped, nothing is deleted.
- **Age of a message:** counted from the moment it was **moved** into `sent\` or `failed\` (the
  modification time of its `.meta.json`), not from when it was received. A message is deleted by
  the first run after it exceeds the retention period, so at most about an hour late. With the
  default of 60 days, a message that failed on 1 March is deleted on 30 April.
- **What is deleted:** the `.eml` and its `.meta.json` together.
- **Changed retention values** apply from the next hourly run, without a restart. To apply them at
  once, restart the service.
- **Archiving switched off:** the cleanup no longer touches `sent\` at all. Messages that were
  archived before you switched it off stay there until you delete them by hand.
- **Failed-mail retention `0`:** `failed\` is never cleaned up. Keep an eye on its size (the
  [failed-queue alert](notifications.md) and [disk-space monitoring](monitoring.md) help).

The same hourly run also moves stray `.eml` files without a matching `.meta.json` (left behind by a
crash while a message was being written, and older than 5 minutes) from `queue\` to `failed\`, and
removes expired records from `mail\blocked\` according to the *Keep records for* settings on the
[Malware Scan](malware-scan.md#limits) and [Message Rules](message-rules.md#keep-records-for-days)
pages.

The log shows one information line per run that deleted something, e.g.
*“Retention cleanup: deleted 3 failed email(s) older than 60 day(s)”*. The individual message ids
appear at Debug level. If a run fails (for example because a file is locked), an error is logged
and the next hourly run tries again.

## Related

- [Messages](../monitoring/messages.md) — browse the queue, sent and failed folders
- [Notifications](notifications.md) — non-delivery reports and failed-queue alerts
- [Monitoring](monitoring.md) — disk-space monitoring for the mail directory
