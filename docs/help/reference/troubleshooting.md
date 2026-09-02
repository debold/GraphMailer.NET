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
> This means the **From** address has no Exchange Online mailbox — typically an on-premises user in a
> hybrid tenant. Microsoft 365 cannot send on its behalf. This is a Microsoft 365 limitation, not a
> GraphMailer defect. Use a sender address that has an Exchange Online mailbox.

If the sender is a **distribution list, a mail-enabled public folder or a mail user**, this is
expected — none of them owns a mailbox. Turn on **Sender Routing** on
[Access Control](../configuration/access-control.md) to send their mail through a relay mailbox
while keeping the original From.

## A sender validation option seems to do nothing

You switched on *Accept groups, public folders and mail users as senders*, but they are still
rejected.

First check that a **relay mailbox** is set under *Sender Routing* — the option stays disabled
without one, because accepting a sender you cannot deliver only turns a clean `550` into a
delayed NDR.

Otherwise: the option needs two Graph permissions that an app registration created before it does not
have.
Run the **Entra ID setup** again on the [Graph API page](../configuration/graph-api.md) — it keeps
the existing registration and certificate and only adds what is missing. The sync status line on
[Access Control](../configuration/access-control.md) names the missing permission, and the service
log records it as an error.

Nothing else is affected while the permission is missing: users and aliases keep validating, and
anything the directory already knew keeps working.

## Mail from a distribution list or public folder is not sent

Depending on where it fails:

- **Rejected at `MAIL FROM` with `550`** — sender validation does not recognise the address. Enable
  *Accept groups, public folders and mail users as senders*, or add a **route** naming the address;
  both are on [Access Control](../configuration/access-control.md). Use **Show synchronised senders**
  there to see what the last sync actually recognised.
- **Accepted, then stuck in the queue with `ErrorSendAsDenied`** — the relay mailbox is not allowed
  to send as that object. The log names the exact `Add-RecipientPermission` command; the **Generate
  SendAs script** button produces it too. Exchange needs **up to an hour** to replicate the grant,
  after which the queued mail goes out on its own.
- **Accepted, then failed with `554 5.2.252 SendAsDenied`** — the From address is outside your
  accepted domains (typical for a mail user pointing at an external address). No permission can
  authorise that; it is spoofing protection.

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

## Malware scanning shows as unavailable

The [Malware Scan](../configuration/malware-scan.md) page reports no AMSI provider, and the log
carries `[MalwareScan] No AMSI provider is registered on this machine`.

Check what is registered:

```powershell
$ids = (Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\AMSI\Providers').PSChildName
$ids | ForEach-Object { Get-ChildItem "HKLM:\SOFTWARE\Classes\CLSID\$_" | Format-Table -AutoSize }
```

- **Nothing listed** — no antivirus product on this machine offers an AMSI provider. Microsoft
  Defender registers one automatically; some third-party products do not. Scanning stays inactive
  until one exists.
- **Listed but nothing is ever detected** — the provider may not be loading. Since Windows 10 1903
  an unsigned provider DLL can be refused; look for Code Integrity events in
  *Applications and Services Logs → Microsoft → Windows → Code Integrity → Operational*.

> [!WARNING]
> Do **not** add `GraphMailer.exe` as an antivirus process exclusion to silence a false positive.
> That disables scanning for the whole service. Allow the specific attachment by its hash on the
> Malware Scan page, or allow the specific detection in your antivirus product — with Defender via
> `Add-MpPreference -ThreatIDDefaultAction_Ids <id> -ThreatIDDefaultAction_Actions Allow`.

## A message was rejected with "content failed malware scan"

The message was flagged and the scan mode is **Enforce**. Nothing was stored, so it cannot be
released — the sender has to send it again after the content is allowed.

Open **Malware Scan → Recent Detections** to see what was found. If it is a false positive in an
attachment, select it and choose **Allow this attachment**, then save. A detection in the message
**body** carries no hash and cannot be allowlisted; the scan bypass is the only option there.

## Related

- [Logs](../monitoring/logs.md) · [Messages](../monitoring/messages.md) · [Status](../monitoring/status.md)
- [FAQ](faq.md) · [Glossary](glossary.md)
