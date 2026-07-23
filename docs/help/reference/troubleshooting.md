# Troubleshooting

Common symptoms and what to do about them. The [Logs](../monitoring/logs.md) page is almost
always the fastest way to find the concrete reason — GraphMailer logs *why* it rejected or failed a
message at **Warning** level or above.

> [!TIP]
> Start here: open **Logs**, filter to **Warning+**, and search for the affected sender or recipient
> address. Most answers below come straight from that log line.

## Mail is accepted but never delivered

The message sits in the queue, or eventually lands in **failed**.

- **Microsoft 365 not connected.** Until the [Entra / Graph Setup](../getting-started/entra-setup.md)
  is complete and admin consent is granted, every delivery attempt fails. Check **Status → System
  Health → Graph API**.
- **Admin consent not granted.** The app can sign in but has no permission to send. Re-run the wizard
  with an administrator who can grant consent.
- **Outage within the retry window.** A transient Microsoft 365 / network problem is normal — the
  message is retried until the expiration window elapses (default 24 h). It only fails permanently
  after that. See [Mail Queue](../configuration/mail-queue.md).

## `MailboxNotEnabledForRESTAPI`

The sender passes validation but delivery fails with this error.

> [!NOTE]
> This means the **From** mailbox has no Exchange Online mailbox — typically an on-premises user in a
> hybrid tenant. Graph cannot send on its behalf. This is a known Microsoft 365 limitation, not a
> GraphMailer defect. Use a sender address that has an Exchange Online mailbox.

## Mail from a sender is rejected at submission

The client gets a rejection during the SMTP conversation.

- **Sender not allowed.** Check the **Allowed/Blocked Senders** lists on
  [Access Control](../configuration/access-control.md).
- **Sender not in the tenant.** If Microsoft 365 sender validation is on, an unknown From address is
  rejected with `550`. Confirm the address exists (a mailbox or alias) in your tenant.
- **From address the tenant doesn't own.** The From must resolve to a real mailbox/alias in your
  tenant — see the [Quickstart](../getting-started/quickstart.md).

## A client cannot connect at all

- **Not whitelisted.** With a non-empty whitelist, only listed IPs/CIDRs may connect. Add the
  client's IP on [IP Filtering](../configuration/ip-filtering.md).
- **Automatically blocked.** Repeated failures (e.g. a wrong password) get an IP temporarily blocked.
  Check **Currently Blocked IPs** and **Unblock** it after fixing the cause.
- **Wrong port / TLS mode.** Confirm the client uses a configured listener port and the matching
  encryption (plain 25, STARTTLS 587, implicit TLS 465). See [Servers & TLS](../configuration/servers-tls.md).
- **Service stopped.** Check the service state on the [Status](../monitoring/status.md) page.

## Authentication keeps failing

> [!NOTE]
> For privacy, the SMTP response to the client is intentionally generic — it never reveals whether
> the username, password, or account state was the problem. The **real reason is in the
> [Logs](../monitoring/logs.md)** (unknown user / wrong password / disabled). Look there.

Also check the user is **Enabled** and the password is correct on
[Access Control](../configuration/access-control.md).

## Connections are not encrypted even though TLS is configured

> [!WARNING]
> If a TLS listener has no usable certificate, GraphMailer logs an **error** and falls back to
> **plain** SMTP on that port to keep mail flowing. Fix the certificate on
> [Servers & TLS](../configuration/servers-tls.md) (select one, or create a self-signed one) and
> restart the service. Watch the [Logs](../monitoring/logs.md) for the certificate error.

## A stored secret shows as blank / a red warning appears

A secret could not be decrypted with the current machine key — common after restoring config to a
different machine. Re-enter the affected secret (on [Graph API](../configuration/graph-api.md) or
[Access Control](../configuration/access-control.md)) and save. See the banner on
[Status](../monitoring/status.md).

## A setting I changed had no effect

Some settings need a restart: **SMTP listeners, the SMTP banner, max message size, the TLS
certificate, the mail directory, and the polling interval.** The toolbar shows a *“Restart
required”* badge — restart from the [Status](../monitoring/status.md) page. Everything else applies
immediately.

## Notifications or reports are not arriving

- A **sender address** must be set and a **recipient** added on
  [Notifications](../configuration/notifications.md).
- The Graph connection must be working (notifications go through Microsoft 365 too).
- The specific event/report must be enabled.

## Related

- [Logs](../monitoring/logs.md) · [Messages](../monitoring/messages.md) · [Status](../monitoring/status.md)
- [FAQ](faq.md) · [Glossary](glossary.md)
