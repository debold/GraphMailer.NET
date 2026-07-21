# Logs

This is the **Logs** page. It reads GraphMailer's rolling log files and lets you filter and search
them — the place to find the concrete reason behind any rejection, failure, or warning.

Logs are written to `C:\ProgramData\GraphMailer\logs\` and roll over automatically. Every entry is
tagged with the component that produced it (for example `[SmtpRelay]`, `[QueueProcessor]`,
`[GraphApi]`).

| File | Contents |
|---|---|
| `graphmailer-*.log` | The service log — everything at or above the configured log level. |
| `error-*.log` | Service errors only (a filtered copy, kept longer for post-incident review). |
| `configtool-*.log` | Diagnostic log of the Config Tool — errors shown in its UI, with full detail. |
| `configtool-crash.log` | Written only if the Config Tool itself crashes. |

> [!NOTE]
> This page is **read-only**. The minimum level that gets *written* is set by the **Log level** on
> the [Monitoring](../configuration/monitoring.html) page.

## Filtering

| Control | Effect |
|---|---|
| Level | Show entries at the chosen level **and above** (e.g. *Warning+* shows Warning, Error, Fatal). |
| Component | Limit to one component (e.g. only SMTP relay or only Graph API entries). |
| Search | Free-text filter across the message text. The **✕** in the box clears it again. |

## Live tail

**Auto-refresh** reloads the log every few seconds and jumps to the newest entry — useful while
reproducing a problem. Turn it off to freeze the view while reading or searching; the **⟳ Refresh**
button still reloads on demand.

## Reading an entry

The list shows **Time**, **Level** (colour-coded), **Component**, and **Message**. Select a row to
open the details panel below the list with the full entry, including any exception detail; right-click
the message to copy it. The **✕** in the top right corner of the panel closes it again.

What the levels mean:

| Level | Meaning |
|---|---|
| Debug | Detailed per-request flow (connections, filter decisions, auth attempts). Only present if the log level is set to Debug. |
| Information | Normal business events (queued, delivered, listener started). |
| Warning | A policy rejection or recoverable anomaly — **always with the concrete reason** (which rule matched, why auth failed, when a block expires). |
| Error | An infrastructure failure needing operator action (cannot write the queue, certificate missing, decryption failure). |
| Fatal | The service cannot continue. |

> [!NOTE]
> Errors always include the full exception detail (stack trace) in the log files, regardless of the
> configured log level — the Debug level adds per-request flow, not more error detail.

> [!TIP]
> Investigating a rejected or failed message? Filter to **Warning+**, then narrow by **Component**
> or search for the sender/recipient address. GraphMailer logs the *reason* for every rejection at
> Warning level.

> [!NOTE]
> For privacy, authentication failure reasons (unknown user, wrong password, disabled account) are
> written to the log only — the SMTP responses sent to clients stay generic. So the log is the
> authoritative place to see *why* a login was refused.

## Related

- [Monitoring](../configuration/monitoring.html) — set the log level (applies immediately)
- [Messages](messages.html) — the message files referenced by delivery log entries
- [Troubleshooting](../reference/troubleshooting.html) — common messages and what to do
