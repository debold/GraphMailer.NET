# Messages

This is the **Messages** page. It lets you browse the actual mail files GraphMailer keeps on disk —
the **queue**, **sent**, and **failed** folders, individually or merged — and inspect any individual
message.

Each message is stored as a `.eml` file with a matching `.meta.json` sidecar under the mail directory
(see [Mail Queue](../configuration/mail-queue.md)).

> [!NOTE]
> This page is **read-only**.

## Choosing a folder

The **Folder** selector switches between:

| Folder | Contents |
|---|---|
| All | All three folders merged into one list, newest first — the default view. An extra **Status** column marks each message as *Queued* (amber), *Sent* (blue) or *Failed* (red). |
| Queue | Messages accepted and waiting to be delivered. |
| Failed | Messages that exhausted the retry window and were given up on. |
| Sent | Successfully delivered messages (only present when archiving is enabled). |

The list reloads itself every few seconds while the page is open.

## How much is loaded, and searching

The list shows the newest **500 messages** at a time. The counter in the toolbar always says what you
are looking at — `500 messages` when that is the whole folder, `newest 500 of 3,214` when more exist.
In the latter case a **Load 500 more** button appears below the list; each click pages further back.
The loaded amount is kept while the list refreshes and resets when you switch folder or type a new
search term.

The **Search** box searches the **whole folder**, not just the loaded messages — sender, recipient,
subject, status, client IP, message id and the failure reason of a failed message. Results appear
shortly after you stop typing, and the counter then reads `12 matches of 3,214`. The **✕** in the box
clears it again.

## The message list

Each row shows **Received**, **From**, **To**, **Subject**, and the number of delivery **Attempts**
(plus **Status** in the **All** folder).
Select a row to open the details panel below the list, showing the full message metadata (headers,
addresses, delivery history, and the last error for failed items). The **✕** in the top right corner
of the panel closes it again.

> [!TIP]
> The **Failed** folder is the one to watch. Messages here will not be retried automatically — they
> have passed their expiration window. Use the detail panel's last-error line, together with
> [Troubleshooting](../reference/troubleshooting.md), to understand why, then have the sender
> resubmit if appropriate.

> [!IMPORTANT]
> Messages contain real recipient content and may include sensitive data and attachments. The mail
> folders are stored under `C:\ProgramData\GraphMailer\` and are access-restricted to
> administrators and the service account — keep them that way.

## Related

- [Mail Queue](../configuration/mail-queue.md) — where these folders live and the retry policy
- [Metrics](metrics.md) — aggregated delivery statistics
- [Troubleshooting](../reference/troubleshooting.md) — interpreting failure reasons
