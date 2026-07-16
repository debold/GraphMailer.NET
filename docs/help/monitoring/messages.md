# Messages

This is the **Messages** page. It lets you browse the actual mail files GraphMailer keeps on disk —
the **queue**, **sent**, and **failed** folders — and inspect any individual message.

Each message is stored as a `.eml` file with a matching `.meta.json` sidecar under the mail directory
(see [Mail Queue](../configuration/mail-queue.html)).

> [!NOTE]
> This page is **read-only**.

## Choosing a folder

The **Folder** selector switches between:

| Folder | Contents |
|---|---|
| Queue | Messages accepted and waiting to be delivered. |
| Sent | Successfully delivered messages (only present when archiving is enabled). |
| Failed | Messages that exhausted the retry window and were given up on. |

A count shows how many messages are in the selected folder; **↻ Refresh** reloads it.

## The message list

Each row shows **Received**, **From**, **To**, **Subject**, and the number of delivery **Attempts**.
Select a row and open the **▼ Details** panel to see the full message metadata (headers, addresses,
delivery history, and the last error for failed items).

> [!TIP]
> The **Failed** folder is the one to watch. Messages here will not be retried automatically — they
> have passed their expiration window. Use the detail panel's last-error line, together with
> [Troubleshooting](../reference/troubleshooting.html), to understand why, then have the sender
> resubmit if appropriate.

> [!IMPORTANT]
> Messages contain real recipient content and may include sensitive data and attachments. The mail
> folders are stored under `C:\ProgramData\GraphMailer\` and are access-restricted to
> administrators and the service account — keep them that way.

## Related

- [Mail Queue](../configuration/mail-queue.html) — where these folders live and the retry policy
- [Metrics](metrics.html) — aggregated delivery statistics
- [Troubleshooting](../reference/troubleshooting.html) — interpreting failure reasons
