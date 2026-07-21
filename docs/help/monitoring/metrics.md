# Metrics

This is the **Metrics** page. It shows mail-traffic statistics and resource usage recorded over time,
so you can see trends and spot problems building up — including behaviour that is hard to see in the
logs, such as clients that connect and disconnect without sending mail. The data comes from the local
statistics database (SQLite) and is the same data the scheduled
[report](../configuration/notifications.html) is built from.

The page is split into six tabs — **Overview**, **Activity**, **Reception**, **Delivery**,
**End-to-End** and **Server** — with a global **time range** selector (**24h / 7d / 30d / 90d**) that
applies to all tabs. The page reloads itself every few seconds while it is open, so there is nothing
to refresh by hand.

> [!NOTE]
> This page is **read-only**. What is recorded (and for how long) is controlled by *Metrics Storage*
> and *Performance Metrics* on the [Monitoring](../configuration/monitoring.html) page.

## Overview

The combined picture of the selected time range:

| Card | Shows |
|---|---|
| Received | Messages accepted over SMTP. |
| Delivered | Messages delivered to Microsoft 365 via the Graph API. |
| Failed | Permanently failed messages (with the permanently-rejected share). |
| Success Rate | Delivered / (delivered + failed). |
| Volume | Total delivered bytes and average message size. |
| Avg. Delivery | Average and maximum time to hand a message to Microsoft 365. |
| Unique Senders | Distinct From addresses, plus the most active one. |
| Queued Now | Messages currently waiting in the mail queue. |

**Mail Flow per Day** charts delivered and failed messages per day (per hour in the 24h range).

## Activity

**Recent Activity** lists the newest per-message events in the selected time range (at most 200)
with timestamp, event type, From, To and subject. **Click an event** to open a details panel below the list showing the rest: size, attachment
count, receiving listener, TLS, authenticated user, duration, and the message id — or the failure
reason for a failed event. Right-click that last line to copy it, for example to paste an error into
a search or a ticket. The **✕** in the top right corner of the panel closes it again.

The **Search** box in the card header filters the list as you type. It matches all of those values,
not just the visible columns, so you can narrow the list down to one sender, one subject or one error
message; the counter next to the box shows how many of the loaded events match. Column widths you
drag and the selected event are kept when the list refreshes itself.

> [!TIP]
> Recent Activity is the fastest way to confirm a specific message went through and how long it took.
> For the actual message file (headers, body, attachments) use the [Messages](messages.html) page;
> for the underlying service events use the [Logs](logs.html) page.

## Reception

How the SMTP side of the relay is being used:

| Card | Shows |
|---|---|
| Sessions | SMTP connections in the range (the service's own health probes are excluded). |
| Aborted (no QUIT) | Sessions the client dropped without saying QUIT, and their share. |
| Rejected | Commands/connections rejected by filters, authentication or policy. |
| TLS Share / Auth Share | How many sessions were encrypted / authenticated. |

- **Aborted Sessions by Last Stage** — at which protocol stage clients disconnect (before HELO,
  after EHLO, after AUTH, after MAIL/RCPT, …). Many aborts right after AUTH are the typical
  fingerprint of a monitoring system checking the relay.
- **Rejections by Reason** — IP blacklist / missing whitelist entry, dynamic IP block, failed
  authentication, blocked sender/recipient, size limit, and more.
- **Top Client Hosts** — sessions, aborted sessions and delivered mails per client IP. Hosts with a
  high aborted share are flagged as possible monitoring probes.
- **Per Listener** — sessions, mails, TLS and auth share per configured listener port, plus the
  average recipients per mail (To/CC/BCC) and the share of mails with attachments.

## Delivery

How messages leave the relay towards Microsoft 365:

| Card | Shows |
|---|---|
| Delivered | Messages delivered via the Graph API. |
| Avg. Delivery Time | Average and maximum Graph send duration. |
| First-Try Rate | Share of messages delivered on the first attempt. |
| Retries / Mail | Average retries across all delivered messages. |

- **Delivery Attempts until Success** — how many messages needed 1, 2, 3 or more attempts.
- **Delivery Variant** — direct `sendMail` versus draft + upload session (used for attachments
  ≥ 3 MB).
- **Top Failure Causes** — the most frequent Graph errors, grouped.

## End-to-End

The complete journey from SMTP receipt to Graph delivery:

| Card | Shows |
|---|---|
| Queue Latency Ø / P95 / Max | Time between SMTP receipt and successful delivery. |
| First-Try Rate | Share of messages delivered on the first attempt. |

- **Message Funnel** — received → delivered / failed / still queued.
- **Permanent Failures** — messages rejected permanently by Graph versus messages that expired
  after the retry window.

## Server

The latest sampled **Memory**, **CPU**, and **Disk Free** values with trend charts over the selected
time range, plus the on-disk size of the statistics database.

> [!NOTE]
> If the charts are empty and you see *“No performance data yet,”* performance sampling is turned
> off. Enable **Performance Metrics** on the [Monitoring](../configuration/monitoring.html)
> page to start recording memory, CPU and disk usage.

> [!NOTE]
> If you see *“No metrics data available,”* the service has not processed any messages yet, or
> metrics recording is disabled on the Monitoring page. Session and rejection statistics start
> being recorded after the first service start with version 1.3 or later.

## Related

- [Monitoring](../configuration/monitoring.html) — enable/tune what is recorded and retention
- [Messages](messages.html) — the actual queued / sent / failed message files
- [Notifications](../configuration/notifications.html) — the scheduled report built from this data
