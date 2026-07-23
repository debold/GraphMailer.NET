# FAQ

## What does GraphMailer actually do?

It is a Windows **SMTP relay**: legacy applications, devices, and scripts on your network send mail
to GraphMailer over plain SMTP, and GraphMailer delivers it to Microsoft 365 through the Graph API.
This lets old software keep sending mail without SMTP AUTH or basic authentication against Exchange
Online.

## Do I still need this now that basic auth / SMTP AUTH is being retired?

That's exactly the use case. Devices and apps that only speak old-style SMTP can keep working: they
talk to GraphMailer locally, and GraphMailer uses a modern, certificate-based Graph connection to
Microsoft 365 on their behalf.

## Does it open any port to the internet?

No. GraphMailer only listens for SMTP on the ports you configure, restricted by
[IP Filtering](../configuration/ip-filtering.html), and it makes **outbound** calls to Microsoft 365.
There is no inbound internet-facing service and no web dashboard — the Configuration Tool is a local
desktop app.

## Can any address send as anyone?

The **From** address must be a real mailbox (or alias) in your tenant. By default the Graph
permissions are tenant-wide, so restrict GraphMailer to specific mailboxes with an Exchange Online
**Application Access Policy** — see [Entra / Graph Setup](../getting-started/entra-setup.html). You
can additionally constrain senders per-user and globally on
[Access Control](../configuration/access-control.html).

## What's the largest message it can send?

Up to **150 MB**, the Microsoft 365 ceiling. The default accepted size is 25 MB; raise it on
[Servers & TLS](../configuration/servers-tls.html) if needed. Messages above 150 MB cannot be
delivered via Graph.

## Which parts of a message survive the relay?

GraphMailer does not forward raw SMTP — it hands each message to Microsoft Graph as a structured
object. Nearly everything a normal message carries comes through, but a few things cannot.

**Carried over:** subject, body, sender, reply-to, all recipients, attachments (including inline
images, so `cid:` references keep working), attached e-mails, importance/priority, private and
confidential markings, read and delivery receipt requests, the Message-ID and the reply/reference
chain that keeps conversations threaded, and up to five custom `X-` headers.

**Cannot be carried over:**

| | Why |
|---|---|
| `Date:` (original composition time) | Microsoft 365 stamps its own send time |
| `Received:` chain, `Return-Path:`, `List-*`, `Auto-Submitted:` | Graph accepts custom headers only with an `X-` prefix |
| More than five `X-` headers | Microsoft 365 limit; the first five are kept |
| S/MIME and PGP signatures | Rebuilding the message invalidates the signature; encrypted mail arrives as an attachment |
| The plain-text half of an HTML message | A message body is either HTML or text; Exchange regenerates the text version |

Anything dropped is written to the log, so it is never silent — S/MIME and skipped recipients as a
warning, the remaining headers at Debug level. See [Logs](../monitoring/logs.html).

## Are messages delivered to addresses in the To: header?

Only to addresses the sending application actually handed over as recipients (`RCPT TO` in SMTP
terms). If an application lists five addresses in the `To:` header but submits the message once per
recipient, each delivery goes to that one recipient — not to all five. This is the correct
behaviour for a relay; anything else would send duplicates. Addresses in a header that were not
submitted as recipients are logged with a warning.

## Do relayed messages appear in the sender's “Sent Items”?

Yes. Every message GraphMailer accepts over SMTP is delivered with a copy kept in the sender
mailbox's **Sent Items** folder in Exchange Online, exactly as if the user had sent it from
Outlook. Mail the service generates itself — non-delivery reports, admin notifications, the
operations report — deliberately leaves no copy, so it does not clutter the mailbox.

## What happens to mail if Microsoft 365 is down?

Nothing is lost. Accepted mail is written to the queue on disk and retried on a schedule until it
succeeds or the expiration window (default 24 h) elapses. Give-up is **by time, not attempt count**,
so an outage inside the window never permanently fails mail. See
[Mail Queue](../configuration/mail-queue.html).

## Are my settings safe to back up and move to another machine?

Yes — use [Backup & Restore](../configuration/backup-restore.html). Backups are encrypted with their
own password and are **not** tied to the machine key, so they restore anywhere. Keep the backup
password safe; without it a backup cannot be restored.

## Where are configuration, logs, and mail stored?

Under `C:\ProgramData\GraphMailer\` — config, logs, the metrics database, and the
queue/sent/failed mail folders. These are access-restricted to administrators and the service
account, and are **not** removed when you uninstall the program.

## Which settings require a service restart?

SMTP listeners, the SMTP banner, max message size, the TLS certificate, the mail directory, and the
polling interval. The toolbar shows a *“Restart required”* badge; everything else applies
immediately.

## Should I use a client secret or a certificate for Microsoft 365?

A **certificate** (the default the wizard sets up). There is no shared secret to leak or rotate on a
schedule, and Entra trusts the exact registered certificate. See
[Graph API](../configuration/graph-api.html).

## How do I send a test email?

On the [Graph API](../configuration/graph-api.html) page, use **Test Email Delivery**: enter a From
(a real tenant mailbox) and a To, and send. It uses the current settings, including unsaved changes.

## Related

- [Quickstart](../getting-started/quickstart.html) · [Troubleshooting](troubleshooting.html) · [Glossary](glossary.html)
