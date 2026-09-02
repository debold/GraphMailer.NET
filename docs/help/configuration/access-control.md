# Access Control

This is the **Access Control** page of the Configuration Tool. It decides **who** may submit mail
through GraphMailer and **which addresses** they may use — SMTP user accounts, allow/block lists
for senders and recipients, and optional validation of senders against your Microsoft 365 tenant.

> [!NOTE]
> Changes on this page apply to the running service **without a restart**. They affect mail
> accepted after you save.

## SMTP Users

Accounts that clients use to authenticate (SMTP AUTH). Whether authentication is offered or
demanded is set per listener on the [Servers & TLS](servers-tls.md) page (Auth = `None` /
`Optional` / `Required`).

Use **+ Add User**, **✎** edit, or **✕** remove. Each user has:

| Field | Meaning |
|---|---|
| Enabled | Turn the account on/off without deleting it. |
| Username | The SMTP login name. |
| Password | The login password — stored **encrypted** (`ENC[…]`), never in plain text. |
| Display Name | A free-text label for your reference. |
| Allowed MAIL FROM | Optional comma-separated addresses or `@domain` patterns this user may send from. Empty = any sender address. |

### Capturing a password instead of typing it

When adding a user you can tick **“Capture password on next SMTP login”** instead of entering a
password. The service then accepts **any** password on that user's first login, stores it
encrypted, and uses it from then on. This is handy when the connecting application already has a
password configured that you do not want to retype.

> [!CAUTION]
> While capture is armed, the **first** login for that user succeeds with any password. Make sure
> the intended application connects before anyone else could, and confirm in the
> [Logs](../monitoring/logs.md) that the expected client authenticated.

> [!NOTE]
> A red warning icon next to a user means its stored password could not be decrypted with the
> current key (for example after restoring config to a different machine). Edit the user, re-enter
> the password, and save to fix it.

## Allowed / Blocked Senders

Control which **MAIL FROM** addresses are accepted:

- **Allowed Senders** — if the list is **empty, all** (authenticated) senders are allowed. If it has
  entries, only matching addresses/patterns may send.
- **Blocked Senders** — addresses/patterns that are **always rejected**, evaluated *after* the allow
  list (a block wins).

Entries can be a full address (`app@corp.com`) or a domain pattern (`@corp.com`).

## Allowed / Blocked Recipients

The same model applied to **RCPT TO** (the destination address):

- **Allowed Recipients** — empty = any recipient domain allowed; otherwise only matching recipients.
- **Blocked Recipients** — always rejected, evaluated after the allow list.

> [!TIP]
> A common pattern for an internal relay: leave senders/recipients open and rely on
> [IP Filtering](ip-filtering.md) plus authenticated users, then add a Blocked list only if you
> need to stop a specific address.

## Microsoft 365 Sender Validation

Optionally check every **MAIL FROM** against your Microsoft 365 tenant *before* accepting the
message, so an unknown sender is rejected immediately with a `550` instead of failing later at
delivery. Aliases are resolved to the owning mailbox for sending.

| Setting | Default | Notes |
|---|---|---|
| Validate senders against the tenant | Off | Master switch for this feature. |
| Reject when validation is unavailable (fail-closed) | Off (fail-open) | See callout below. |
| Refresh interval (minutes) | `60` | How often the tenant directory is re-synced. Range 1–1440 (24 h). New mailboxes are also found on demand between syncs. |

Only addresses in your tenant's **mail domains** count as senders. This matters in a hybrid setup:
directory synchronisation copies the on-premises address list into Microsoft 365 unchanged, so a
mailbox routinely carries an address in the internal AD namespace (`user@ad.corp.com`) next to its
real ones. Those domains carry no mail — Exchange will not send from them and refuses even a SendAs
grant over one — so they are left out of the directory rather than accepted and rejected later.

Senders that own no mailbox — distribution lists, public folders, mail users — are handled entirely
in **Sender Routing** below, including whether validation recognises them.

The **⟲ Sync now** button asks the running service to re-sync the directory immediately (available
while validation is enabled and the service is running). The status line below it reports what the
last sync found — and, in red, anything it had to skip.

> [!IMPORTANT]
> Sender validation requires the **`User.Read.All`** and **`Domain.Read.All`** application
> permissions on the Entra app registration — the
> [Entra setup wizard](../getting-started/entra-setup.md) grants both. Without `Domain.Read.All`
> validation keeps working, but the mail domains stay unknown, so the addresses described above
> cannot be filtered out and the sync status line says so.

> [!WARNING]
> **Fail-open vs. fail-closed.** By default (fail-open), if Microsoft 365 is unreachable or the
> permission is missing, senders are accepted *unvalidated* and an admin notification is sent — mail
> keeps flowing. Enabling **fail-closed** rejects with `550` instead, which is stricter but means a
> Microsoft 365 / Entra outage will **stop mail acceptance**. Choose deliberately.

## Sender Routing

Microsoft 365 only accepts a **real mailbox** as the sending account. Three sender types have none
of their own and are rejected without this feature:

- **distribution lists** and mail-enabled security groups,
- **mail-enabled public folders**,
- **mail users** (directory accounts without an Exchange Online mailbox).

GraphMailer sends their mail through a **relay mailbox** instead, keeping the original address in
the `From` header. Recipients see the group or public folder as the sender — there is no "on behalf
of" note. Exchange authorises this via the **SendAs** permission, which has to be granted on each
object to the relay mailbox.

| Setting | Default | Notes |
|---|---|---|
| Send as recipients that have no mailbox of their own | Off | Master switch for this feature. |
| Relay mailbox | *(empty)* | UPN or primary SMTP address of a real mailbox. A **shared mailbox is enough** and needs no licence. |
| Routes | *(empty)* | Optional. Only needed to pin a sender to a different mailbox, or to name senders Microsoft 365 cannot report. Same patterns as the sender lists: `reports@contoso.com` or `@lists.contoso.com`. |

A sender without a mailbox is relayed automatically: the direct attempt and the relay retry happen
**within the same delivery attempt**, so the message still goes out on its first try, and the result
is remembered so later mail from the same address skips the failing request.

A sender covered by a route is always accepted at **MAIL FROM**, even when sender validation would
not recognise it.

### Recognising these senders

One more option decides whether sender validation lets these senders through in the first place. It
stays disabled until a relay mailbox is set — **deliberately**: without one it would let a group
through `MAIL FROM` only for Microsoft 365 to reject it a moment later, turning a clean `550` during
the SMTP session into a delayed NDR. It also only matters while sender validation is on; with it
off, every sender is accepted anyway.

| Setting | Default | Notes |
|---|---|---|
| Accept groups, public folders and mail users as senders | Off | Requires the extra **`Group.Read.All`** permission (`Domain.Read.All` is already needed by sender validation itself). |

It works in two steps, because Microsoft 365 exposes these sender types very differently:

- **Groups** — distribution groups, mail-enabled security groups and Microsoft 365 groups (the kind
  behind every Team) are read into the directory and matched by address, exactly like mailboxes.
  Each still needs its own SendAs grant; the generated script covers all of them.
- **Public folders and dynamic distribution groups** — neither exists in Microsoft 365's API at all,
  so an address is accepted when its **domain** is one of your tenant's verified mail domains. That
  list comes from Microsoft 365 directly, so it includes the coexistence domain
  `<tenant>.mail.onmicrosoft.com` that hybrid setups rely on.

That second step is weaker than an address-exact check: a made-up address in one of your own domains
passes too. Every external sender is still rejected. If you would rather name these senders
explicitly, leave the option off and give each one a **route** instead.

> [!NOTE]
> The domain list is asked for, not inferred from your users' addresses — deliberately. An
> unlicensed directory account has no mailbox and no Exchange object behind it, yet can carry any
> address at all in its alias list, including one in a domain you do not own. Microsoft 365 offers
> nothing that tells such an account apart from a genuine mail user, so inferring domains from
> addresses would either let a foreign domain in or drop real recipients.
>
> The blind spot is a verified domain that carries no mail service; senders there need a route.
> **B2B guests are excluded from the sync** entirely — they have no mailbox in your tenant.

> [!IMPORTANT]
> **After an update, switching this on is not enough.** It needs an extra Graph permission, and an
> app registration created before this version does not have it. Go to the
> [Graph API page](graph-api.md) and run the **Entra ID setup** again: it keeps the existing
> registration and certificate and only adds the permissions that are missing.
>
> If you forget, nothing breaks — the option simply has no effect. The rest of the directory keeps
> syncing normally, the groups that were already known keep working, and the sync status line names
> the missing permission. You also get an admin notification, and the service log records it as an
> error.

### Checking what was recognised

**Show synchronised senders** opens a read-only list of every mailbox and group the last sync
found, with their display name, primary address and all aliases — one address per line, so a
mailbox with a dozen aliases stays readable. Sort by any column, and filter by typing: the filter
searches the name, the primary address and every alias, so you can confirm that a specific alias
really is known before wondering why a sender is rejected.

The rows of type **Domain** are the tenant mail domains derived from those mailboxes and groups.
They are what lets a mail-enabled public folder or a dynamic distribution group through, and this
list is the only place they are visible — worth a look if such a sender is rejected unexpectedly.

Public folders and dynamic distribution groups themselves never appear: Microsoft 365 does not
expose them as objects, which is exactly why they are matched by domain or by a route instead.
**B2B guests are not listed either** — they have no mailbox in your tenant and can never be a
sender.

> [!NOTE]
> **Sign-in names are not sender addresses.** An account's user principal name is only *supposed*
> to match its primary address; an account synced from on-premises Active Directory usually keeps
> the internal AD suffix (`user@ad.corp.com`) instead, and that suffix carries no mail at all. Only
> the mailbox's real addresses — primary and aliases — count as senders and are listed here. If a
> sender is rejected and you expected the sign-in name to work, use the address shown in this list.

### Granting SendAs

Microsoft 365's API can neither grant nor read this permission, so it is set once in Exchange Online
PowerShell. The **Generate SendAs script** button builds the script for you — choose *all
mail-enabled objects* (re-runnable, and the only variant that reaches public folders) or *only the
objects I select*, then copy it or save it as a `.ps1` and run it as an Exchange administrator. The
script ends with a read-back of what is actually granted, plus a granted/skipped/failed summary.

It walks every recipient type separately, because Exchange keeps them apart: ordinary distribution
groups, **Microsoft 365 groups** (what a Team is), dynamic distribution groups, mail-enabled public
folders and mail users. A group that is a Team therefore needs the script just like any other — it
has a mailbox of its own, but nothing can send as it until the relay mailbox holds SendAs on it.

Some lines in that output are expected rather than problems:

- **`skip … is not an accepted domain`** — one of the recipient's addresses lies outside your
  tenant's domains. Exchange refuses SendAs for those whatever you do; mail users pointing at an
  external address and B2B guests all land here. Both the primary address and the external address
  a mail user forwards to are checked, because either one is enough for Exchange to refuse.
- **`failed …`** for a single object — Exchange considers that object inconsistent. The rest of the
  run is unaffected; fix the object separately if you need it as a sender.
- **`… could not be listed`** — that recipient type is unavailable in your tenant. Public folders
  report this when they live on-premises; give those senders a route instead.

The core command, per object:

```powershell
Add-RecipientPermission -Identity "<group, public folder or mail user>" `
                        -Trustee "<relay mailbox>" -AccessRights SendAs
```

> [!IMPORTANT]
> Exchange replicates permission changes lazily — allow **up to an hour** before testing. Until the
> permission is live, affected mail stays in the queue and is retried; it is not bounced. The log
> names the missing permission and the exact command.

> [!NOTE]
> **Mail users pointing at an external address cannot be used.** If the address is outside your
> accepted domains, Exchange rejects the send with `554 5.2.252 SendAsDenied` no matter what
> permissions you grant — that is spoofing protection, not a permission problem. Mail users whose
> primary address is inside your tenant work normally.

Relayed mail is **not** copied into the relay mailbox's Sent Items: the copy would not belong to the
actual sender, and a group or public folder has no Sent Items at all. (Messages with attachments
of 3 MB or more take a different delivery path where Microsoft 365 always keeps a copy.)

## Related

- [Servers & TLS](servers-tls.md) — where per-listener authentication is enabled
- [IP Filtering](ip-filtering.md) — network-level access control
- [Entra / Graph Setup](../getting-started/entra-setup.md) — the `User.Read.All` permission
