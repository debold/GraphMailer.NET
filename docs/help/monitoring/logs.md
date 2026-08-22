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
> the [Monitoring](../configuration/monitoring.md) page.

## Filtering

| Control | Effect |
|---|---|
| Level | Show entries at the chosen level **and above** (e.g. *Warning+* shows Warning, Error, Fatal). |
| Component | Limit to one component (e.g. only SMTP relay or only Graph API entries). |
| Search | Free-text search across the message, the component and any stack trace. The **✕** in the box clears it again. |

All three search the **whole retained history** — every `graphmailer-*.log` file still on disk, not
only the entries currently on screen. Results appear shortly after you stop typing.

The *Component* dropdown lists every component seen while reading, so narrowing to one component
never removes the others from the list.

## How much is loaded

The page loads the newest **2,000 entries** at a time. The counter in the toolbar always says what
you are looking at: `newest 2,000 entries` while more history is available, and `47 matches of
12,430` once a filter is active — the second number being how many entries were examined. When more
exists, a **Load 2,000 more** button appears below the list; each click reaches further back. The
loaded amount is kept while the page auto-refreshes and resets when you change a filter.

On a very large log a filtered search stops after 25,000 entries and says so in the counter rather
than presenting a partial result as complete. Narrow the level or component to search further back.

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

## The row context menu

**Right-click a row** for the actions on that entry.

**Copy entry** puts the complete entry on the clipboard — timestamp, level, component, message and
any stack trace — which is the form worth pasting into a ticket or a mail. (To copy just the message
text, right-click it in the details panel below the list instead.)

Below that, the menu offers every IP address the entry mentions — a rejection line usually names
both the client and the rule it matched — with a *whitelist* and a *blacklist* choice for each.
Entries with no address show a disabled *No IP address in this entry*.

Picking one switches to the [IP Filtering](../configuration/ip-filtering.md) page and opens the
entry dialog with the address filled in, so you can widen it to a CIDR range and add a comment
before confirming. If the address is already on that list, the existing row is selected instead.

> [!IMPORTANT]
> The entry is **not saved yet** — it is added to the page, and you still have to press *Save*. Once
> saved it takes effect without a service restart, but a session that is already open is not
> disconnected.

Version numbers, timestamps and similar dotted or colon-separated values in a log line are not
offered: a candidate has to be a real address to appear in the menu.

## Related

- [Monitoring](../configuration/monitoring.md) — set the log level (applies immediately)
- [Messages](messages.md) — the message files referenced by delivery log entries
- [Troubleshooting](../reference/troubleshooting.md) — common messages and what to do
