# Entra / Graph Setup

Before GraphMailer can deliver anything, Microsoft 365 has to **trust it**. You register
GraphMailer as an application in your **Microsoft Entra ID** (formerly Azure AD) tenant and grant
it permission to send mail through the Graph API. This page is done once per tenant.

There are two ways to do it, both on the **Graph API** page of the Configuration Tool:

- the **automatic setup wizard** (recommended — a few clicks), or
- **manual configuration**, if your organisation registers the app for you.

## What you need

- An account with the **Application Administrator** or **Global Administrator** role in your tenant.
  The wizard signs in with this account in your browser.
- The ability to grant **admin consent** for the application permissions GraphMailer requests.

> [!IMPORTANT]
> The permissions GraphMailer needs are *application* permissions, which always require an
> administrator to consent on behalf of the organisation. If you sign in with a normal user
> account, the wizard cannot finish. The connection appears configured but **every delivery
> fails** until consent is granted.

## Option A — Automatic setup (recommended)

On the **Graph API** page, click **“Sign in & set up automatically.”** A browser window opens for
you to sign in. The wizard then performs these steps and shows the progress of each:

1. **Sign in to Microsoft Entra ID** — in your system browser (times out after 3 minutes).
2. **Check for an existing app registration** — reuses one named *GraphMailer* if it already exists.
3. **Generate a self-signed certificate** — `GraphMailer Entra Auth`, valid for **2 years**, stored
   in the Windows certificate store (`LocalMachine\My`).
4. **Create the app registration** in your tenant.
5. **Create the service principal** for that app.
6. **Grant the Graph permissions** — `Mail.Send`, `Mail.ReadWrite`, `User.Read.All` (with admin
   consent).
7. **Apply the configuration** — Tenant ID, Client ID and certificate are written to the service
   config for you.

When it finishes, the page shows the result: app name, Tenant ID, Client ID, certificate subject,
thumbprint and expiry date.

### Why these three permissions?

| Permission | Why GraphMailer needs it |
|---|---|
| `Mail.Send` | Sending mail — the core function. |
| `Mail.ReadWrite` | Attachments **≥ 3 MB**. Graph caps a direct send at 4 MB, so large mail is uploaded as a draft, which is a mailbox write operation. |
| `User.Read.All` | Optional sender validation — checking that a *From* address belongs to a real mailbox in your tenant. |

> [!CAUTION]
> By default these permissions apply to **every mailbox in the tenant** — the app could send as
> any user. This is standard for Graph application permissions. To limit GraphMailer to only the
> mailboxes it should use, ask your Exchange administrator to apply an **Application Access Policy**
> in Exchange Online (`New-ApplicationAccessPolicy`). This is strongly recommended for production.

## Option B — Manual configuration

Expand **Manual Configuration** on the Graph API page if the app registration is managed for you.
Fill in:

- **Tenant ID** — your Entra tenant GUID (Azure Portal → Entra ID → Overview).
- **Client ID (App ID)** — the registered application's ID (App registrations → your app → Overview).
- **Authentication**, one of:
  - **Client Secret** — a secret created in *Certificates & secrets*. It is stored
    **encrypted** (`ENC[…]`) in the config, never in plain text.
  - **Certificate** — select a certificate from the Windows store (`LocalMachine\My`, Client
    Authentication). Its thumbprint is saved and used to locate it at runtime.

> [!NOTE]
> Certificate authentication is preferred over a client secret: there is no shared secret to leak
> or rotate on a schedule, and Entra trusts the exact registered certificate. If you use a
> certificate, it must also be uploaded to the app registration in Entra.

See the [Graph API](../configuration/graph-api.html) reference for every field on this page.

## Send a test email

The **Test Email Delivery** card lets you verify the connection immediately — enter a **From**
address (a licensed mailbox in your tenant) and a **To** address, then **Send test email**. It uses
the current settings, including unsaved changes, so you can confirm everything before saving.

> [!WARNING]
> The **From** address must be a real Microsoft 365 mailbox (or one of its aliases). A test from an
> address your tenant does not own will be rejected.

## Renewing the certificate

The setup certificate is valid for **2 years**. Before it expires, return to the Graph API page and
run the renewal: it signs you in, generates a fresh certificate, uploads it to the existing app
registration in Entra, and updates the config — no new app registration, no downtime.

> [!TIP]
> The [Health Checks](../configuration/health-checks.html) page and the scheduled
> [report](../configuration/notifications.html) warn you as the certificate approaches expiry, so
> renewal never takes you by surprise.

## Troubleshooting

- **Sign-in window times out** — it waits 3 minutes; start the wizard again.
- **Permissions look granted but mail fails** — confirm an administrator granted *admin consent*
  in step 6.
- **`MailboxNotEnabledForRESTAPI`** — the sender has no Exchange Online mailbox (e.g. an on-premises
  hybrid user). See [Troubleshooting](../reference/troubleshooting.html).
