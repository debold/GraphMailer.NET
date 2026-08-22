# Servers & TLS

This is the **Servers & TLS** page of the Configuration Tool. It controls how applications connect
to GraphMailer over SMTP: which ports it listens on, whether those connections are encrypted, the
TLS certificate it presents, and the maximum message size.

> [!IMPORTANT]
> Every setting on this page takes effect only after the service **restarts**. The Configuration
> Tool shows a *“Restart required”* badge after you save changes here. Mail already accepted is not
> affected by the restart.

## SMTP Listeners

A *listener* is one TCP port GraphMailer accepts mail on. A fresh install starts with three,
matching the industry-standard SMTP ports:

| Port | Description | TLS Mode | Auth | Purpose |
|---|---|---|---|---|
| `25` | SMTP (Plain) | None | None | Plain submission from internal apps/devices |
| `465` | SMTPS (Implicit TLS) | Implicit TLS | Optional | Encrypted from the first byte |
| `587` | Submission (STARTTLS) | STARTTLS | Optional | Upgrades to TLS after connecting |

Use **+ Add Listener**, the **✎** edit button, or the **✕** remove button to manage them. Each
listener has these fields:

| Field | Meaning |
|---|---|
| Enabled | When off, the listener is kept in the config but not started. |
| Description | A free-text label shown in the list — no effect on delivery. |
| Port | The TCP port to listen on. |
| TLS Mode | `None` (plaintext), `STARTTLS (optional)`, `STARTTLS (required)`, or `Implicit TLS`. |
| Authentication | `None`, `Optional`, or `Required` — whether clients may / must sign in. See [Access Control](access-control.md). |

> [!NOTE]
> `STARTTLS (optional)` lets a client choose to encrypt; `STARTTLS (required)` refuses to accept
> mail until the client upgrades to TLS. `Implicit TLS` expects the connection to be encrypted
> immediately (the classic port 465 behaviour).

**TLS versions** are not configurable, and deliberately so: encrypted listeners accept **TLS 1.2 and
TLS 1.3**, and nothing older. TLS 1.3 needs Windows Server 2022 / Windows 11 or newer; on earlier
Windows versions the listener offers TLS 1.2 only. GraphMailer does not follow the machine's
Schannel settings here — on a server where TLS 1.0 or 1.1 is still switched on for some other
application, the mail listeners stay at 1.2 as their lowest version regardless.

> [!WARNING]
> Plaintext (`None`) listeners and `Optional` authentication rely entirely on the
> [IP Filtering](ip-filtering.md) allow-list to decide who may relay mail. Only keep port 25
> plain on a trusted internal network. For anything reachable more widely, require TLS and
> authentication.

## TLS Certificate (Server Authentication)

Any listener using STARTTLS or Implicit TLS needs a certificate to present to clients. GraphMailer
selects it from the Windows certificate store (`LocalMachine\My`) — you pick it from the list on
this page.

- Selection is **by subject name** (and optional issuer); when a renewed certificate with the same
  subject appears, the one with the latest expiry is used automatically. This makes certificate
  **renewal seamless** — no need to reselect after renewal.
- The **+ Create self-signed certificate** button generates a new self-signed certificate
  (`CN=GraphMailer SMTP`, RSA-2048, valid 10 years) and installs it in `LocalMachine\My`, ready to
  select. On a brand-new install GraphMailer already created one of these for you.

> [!TIP]
> A self-signed certificate encrypts the connection but clients cannot verify it against a trusted
> authority, so they may warn or need to “trust” it. For wider trust, install a certificate from
> your internal CA (or a public CA) into `LocalMachine\My` and select it here.

> [!CAUTION]
> If a TLS listener is configured but **no certificate can be loaded**, GraphMailer logs an error
> and falls back to **plain (unencrypted)** SMTP on that port rather than refusing all mail. This
> keeps mail flowing during a certificate problem, but means traffic is briefly unencrypted —
> watch the [Logs](../monitoring/logs.md) and [Monitoring](monitoring.md) for this.

### Do not start TLS listeners without a certificate (fail-closed)

Default **off**. When enabled, the plain-SMTP fallback described above is disabled: a TLS or
STARTTLS listener whose certificate cannot be loaded is **not started at all** — the port stays
closed until a certificate is available (the service logs an error naming the listener). Plain
listeners are unaffected.

Choose this when confidentiality matters more than availability: credentials and mail content can
then never cross the wire unencrypted, but clients cannot deliver on the encrypted ports while the
certificate is missing. Changing the option requires a **service restart**.

## Limits

| Setting | Default | Notes |
|---|---|---|
| Max message size (MB) | `25` | Largest message accepted during SMTP DATA. Range 1–150. |
| Max recipients per message | `500` | Most envelope recipients accepted for one message. Range 1–1000. |
| SMTP banner | `GraphMailer` | The name announced in the SMTP greeting (EHLO). Cosmetic — does not affect delivery. |

Both limits mirror settings **your tenant owns**, and GraphMailer cannot read them: no Microsoft
Graph permission the service holds exposes them, and one that did would mean granting the relay
tenant-wide Exchange administration rights. So you configure them here to match your tenant.

### Reading your tenant's real values

In [Exchange Online PowerShell](https://learn.microsoft.com/powershell/exchange/connect-to-exchange-online-powershell),
ask the mailbox GraphMailer sends as:

```powershell
Connect-ExchangeOnline
Get-Mailbox relay@contoso.com | Format-List MaxSendSize, RecipientLimits
```

```
MaxSendSize     : 35 MB (36,700,160 bytes)
RecipientLimits : Unlimited
```

- **`MaxSendSize`** → the *Max message size* value. Round down to whole MB (`35 MB` → enter `35`).
- **`RecipientLimits`** → the *Max recipients per message* value. **`Unlimited` does not mean
  unlimited** — it means the mailbox uses the Exchange Online service default of **500**, so enter
  `500`. A number means the administrator set a custom limit (Microsoft allows 1–1000).

Different senders can have different limits. If GraphMailer relays for several mailboxes, take the
**highest** value of each — for the same reason:

> [!TIP]
> **When unsure, set both higher rather than lower.** Too high only means Exchange rejects the
> message itself — GraphMailer reports the rejection and sends an NDR, and you can correct the
> setting. Too low means GraphMailer refuses mail that your tenant would have accepted, and no
> Exchange setting can rescue it: the sender simply gets a rejection for a message that was fine.

> [!NOTE]
> Microsoft 365 enforces a hard **150 MB** ceiling on message size regardless of `MaxSendSize`, so
> setting the limit higher cannot help — messages above 150 MB are undeliverable via Graph and the
> service warns about it at startup. Internally, messages up to ~3 MB are sent directly; larger ones
> are uploaded in a streaming session.

## Related

- [Access Control](access-control.md) — who may authenticate and which senders are allowed
- [IP Filtering](ip-filtering.md) — which networks may connect at all
- [Monitoring](monitoring.md) — alerts when a certificate is missing or expiring
