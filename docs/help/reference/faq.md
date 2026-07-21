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
