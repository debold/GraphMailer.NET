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

The **⟲ Sync now** button asks the running service to re-sync the directory immediately (available
while validation is enabled and the service is running).

> [!IMPORTANT]
> Sender validation requires the **`User.Read.All`** application permission on the Entra app
> registration — the [Entra setup wizard](../getting-started/entra-setup.md) grants it.

> [!WARNING]
> **Fail-open vs. fail-closed.** By default (fail-open), if Microsoft 365 is unreachable or the
> permission is missing, senders are accepted *unvalidated* and an admin notification is sent — mail
> keeps flowing. Enabling **fail-closed** rejects with `550` instead, which is stricter but means a
> Microsoft 365 / Entra outage will **stop mail acceptance**. Choose deliberately.

## Related

- [Servers & TLS](servers-tls.md) — where per-listener authentication is enabled
- [IP Filtering](ip-filtering.md) — network-level access control
- [Entra / Graph Setup](../getting-started/entra-setup.md) — the `User.Read.All` permission
