# Graph API

This is the **Graph API** page of the Configuration Tool — where GraphMailer's connection to
Microsoft 365 lives. It has three parts: the **automatic setup wizard**, a **test email** tool, and
**manual configuration**.

If you have not connected your tenant yet, start with the step-by-step
**[Entra / Graph Setup](../getting-started/entra-setup.html)** guide; this page is the field-level
reference.

> [!NOTE]
> Connection changes here apply to the running service **without a restart**.

## Automatic Entra ID Setup

The **“Sign in & set up automatically”** button runs the wizard that registers GraphMailer in your
tenant, generates an authentication certificate, grants the required permissions, and fills in all
the settings below for you. This is the recommended path and is documented in full on the
[Entra / Graph Setup](../getting-started/entra-setup.html) page.

After a successful run, the page shows the result: app name, Tenant ID, Client ID, certificate
subject, thumbprint and expiry date.

## Test Email Delivery

Sends a test message with the **current** settings (including unsaved changes), so you can confirm
the connection before saving.

- **From** — a sender address; must be a licensed mailbox in your tenant.
- **To** — any recipient address.

> [!WARNING]
> The **From** address must be a real Microsoft 365 mailbox (or one of its aliases). A test from an
> address the tenant does not own is rejected.

## Manual Configuration

Expand this section if the app registration is managed for you instead of by the wizard.

| Field | Meaning |
|---|---|
| Tenant ID | Your Entra tenant GUID (Azure Portal → Entra ID → Overview). |
| Client ID (App ID) | The registered application's ID (App registrations → your app → Overview). |
| Authentication | Either a **Client Secret** or a **Certificate** (see below). |

### Authentication: secret or certificate

- **Client Secret** — a secret string created in *Certificates & secrets*. Stored **encrypted**
  (`ENC[…]`) in the config, never in plain text.
- **Certificate** — selected from the Windows store (`LocalMachine\My`, Client Authentication). Its
  **thumbprint** is saved in the config and used to locate it at runtime. The certificate must also
  be uploaded to the Entra app registration.

> [!IMPORTANT]
> When both a secret and a certificate are configured, the **certificate takes precedence**.
> Certificate authentication is preferred: nothing secret is stored that could leak, and Entra
> trusts the exact registered certificate.

> [!NOTE]
> Selecting a certificate by **thumbprint** (what the picker stores) is recommended over selecting
> by subject name. Entra trusts the one specific certificate you registered; a subject-name match
> would auto-pick the newest certificate, which may not yet be registered in Entra. Subject-name
> selection exists mainly for zero-downtime rotation where *both* certificates are pre-registered.

## Required permissions

GraphMailer needs three Microsoft Graph **application** permissions, all granted by the wizard:

| Permission | Used for |
|---|---|
| `Mail.Send` | Sending mail (core function). |
| `Mail.ReadWrite` | Large attachments (≥ 3 MB) uploaded as a draft. |
| `User.Read.All` | Optional sender validation against the tenant. |

> [!CAUTION]
> These permissions apply tenant-wide by default. Restrict GraphMailer to only the mailboxes it
> should use with an Exchange Online **Application Access Policy** — see the
> [Entra / Graph Setup](../getting-started/entra-setup.html) guide.

## Troubleshooting

- **Test mail fails with `MailboxNotEnabledForRESTAPI`** — the sender has no Exchange Online mailbox
  (e.g. an on-premises hybrid user). See [Troubleshooting](../reference/troubleshooting.html).
- **Authentication errors after setup** — confirm an administrator granted admin consent, and that
  the certificate (or secret) in the config matches the one registered in Entra.
- **Certificate expiring** — renew it from this page; see
  [Entra / Graph Setup → Renewing the certificate](../getting-started/entra-setup.html).

## Related

- [Entra / Graph Setup](../getting-started/entra-setup.html) — full setup and renewal walkthrough
- [Access Control](access-control.html) — Microsoft 365 sender validation (`User.Read.All`)
- [Monitoring](monitoring.html) — alerts for permission gaps and certificate expiry
