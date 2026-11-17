# Messages

This is the **Messages** page. It answers the question that comes up after every outage: *was this
mail delivered, and when?* The list on the left shows the recent messages; select one to see its
details and a **timeline** on the right, from the SMTP receipt through the malware scan and the
message rules to every delivery attempt and the result.

> [!NOTE]
> This page is **read-only**.

## Where the data comes from

Since version 1.6.0 the service records each step of every message in its statistics database
(`metrics.db`, see [Monitoring → Metrics Storage](../configuration/monitoring.md#metrics-storage)).
This history is kept for the metrics **retention** (90 days by default), so it outlives the message
files: a delivered message whose `.eml` was cleaned out of `sent\` after 7 days still shows when
and how it went out.

The page also reads the mail folders (`queue`, `sent`, `failed` under the mail directory, see
[Mail Queue](../configuration/mail-queue.md)). Messages that were handled **before 1.6.0** are only
known from there. For them the timeline is pieced together from what the `.meta.json` file and the
receipt statistics still tell: when and from where the message came in, how many attempts failed
and the last error, and the result. A yellow note above the details says so.

> [!NOTE]
> With **Enable metrics** switched off, no history is recorded. The page then shows only the
> messages in the mail folders, each with the reduced timeline.

## Choosing what to show

The **Show** selector filters the list:

| Filter | Shows |
|---|---|
| All | Every message, newest first — the default view. |
| Queue | Accepted and still waiting for delivery. |
| Delayed | Needed more than one delivery attempt: delivered late, or still being retried. The one to open after a Microsoft 365 outage. |
| Failed | Accepted, but GraphMailer gave up: Microsoft 365 rejected the message for good, or the retry window ran out. |
| Rejected | Refused during the SMTP session (malware, a rule, a queue problem), or discarded by a rule. |
| Sent | Delivered to Exchange Online. |

The **Status** column marks each message: *Queued* and *Sent late* (amber), *Sent* (blue), *Failed*,
*Rejected* and *Discarded* (red). The list reloads itself every few seconds while the page is open.

## How much is loaded, and searching

The list shows the newest **500 messages** at a time. The counter in the toolbar says what you are
looking at — `500 messages` when that is everything, `newest 500 of 3,214` when more exist. In the
latter case a **Load 500 more** button appears below the list; each click pages further back. The
loaded amount is kept while the list refreshes and resets when you change the filter or type a new
search term.

The **Search** box searches **all** messages, not just the loaded ones — sender, recipient, subject,
status, client IP, message id, and the steps of a message's history (an error code like `503` finds
every message that hit it). Results appear shortly after you stop typing. The **✕** in the box
clears it again.

## Details and timeline

Select a row to open the details on the right. The **✕** in their top right corner closes them again.

- **Summary.** One or two sentences that answer the question, for example *Delivered on 12.11.2026
  at 10:50, 36 minutes after it arrived. 2 attempts failed before that, the last one with: 504
  GatewayTimeout.* **Copy summary** puts it on the clipboard together with subject, sender,
  recipient and times — ready for a reply to whoever asked.
- **Facts.** Sender, recipients, receipt and delivery time, client, attempts, the next retry of a
  queued message, the Message-ID and GraphMailer's own queue id. Right-click a value to copy it.
- **Timeline.** One line per step, grouped into **SMTP**, **Processing** and **Delivery**, with the
  time and a coloured dot: green for done, amber for a failed attempt or a warning (such as a malware
  finding in audit mode), red where the message stopped. A failed attempt shows the error Microsoft
  365 returned and when the next attempt was due. A long run of failed attempts is shortened to the
  first and last two, with the number of hidden ones in between.

> [!TIP]
> A message in **Failed** will not be retried. Its last timeline line names the reason; together
> with [Troubleshooting](../reference/troubleshooting.md) that usually tells you what to fix before
> the sender resubmits.

> [!IMPORTANT]
> Messages contain real recipient content and may include sensitive data and attachments. The mail
> folders and `metrics.db` are stored under `C:\ProgramData\GraphMailer\` and are access-restricted
> to administrators and the service account — keep them that way.

## Related

- [Mail Queue](../configuration/mail-queue.md) — where the mail folders live and the retry policy
- [Monitoring](../configuration/monitoring.md) — metrics storage and its retention
- [Metrics](metrics.md) — aggregated delivery statistics
- [Troubleshooting](../reference/troubleshooting.md) — interpreting failure reasons
