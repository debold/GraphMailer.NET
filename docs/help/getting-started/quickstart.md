# Quickstart — your first 15 minutes

This page walks you through everything that needs to happen between *"GraphMailer is installed"*
and *"my application can send mail through it"*. Follow the steps in order.

> [!NOTE]
> This is the short, happy-path version. Each step links to the detailed reference page for the
> matching ConfigTool screen if you need more background.

## Before you begin

You need:

- An account that can install software and register a Windows service (local administrator).
- A Microsoft 365 tenant where you may register an application (Global Administrator, or an
  Application Administrator who can grant admin consent).
- The IP address(es) of the application(s) that will hand mail to GraphMailer.

## Step 1 — Confirm the service is running

After [installation](installation.html), open the **GraphMailer Configuration Tool** from the
Start menu. The bottom-left of the window shows the service state.

- A green dot and **Running** mean the service started on the bundled defaults.
- A grey or red dot means it is stopped — use the **Status** page to start it.

On first start GraphMailer creates its data directory and seeds a default configuration,
including SMTP listeners on ports 25, 465 and 587 and a self-signed TLS certificate.

> [!TIP]
> Nothing is sent to Microsoft 365 yet. Until you complete Step 3, mail that arrives is accepted
> and queued, then retried — it is never silently dropped.

## Step 2 — Allow your application's IP address

Open **IP Filtering**. By default only private network ranges are allowed to connect. Add the
IP address of each application or device that will relay mail, then **Save**.

See [IP Filtering](../configuration/ip-filtering.html) for the default ranges and how automatic
blocking works.

## Step 3 — Connect GraphMailer to Microsoft 365

Open **Graph API** and run the **Entra setup wizard**. It registers an application in your tenant,
requests the required permissions (`Mail.Send`, `Mail.ReadWrite`, `User.Read.All`), and stores the
connection details for you.

> [!IMPORTANT]
> The wizard needs an administrator who can grant **admin consent** for the requested permissions.
> Without consent, GraphMailer can authenticate but every delivery attempt fails. The full
> walkthrough is on the [Entra / Graph Setup](entra-setup.html) page.

## Step 4 — Point your application at GraphMailer

Configure your application's SMTP settings to the GraphMailer host:

| Setting | Value |
|---|---|
| Server / host | The Windows machine running GraphMailer |
| Port | `25` (plain), `587` (STARTTLS), or `465` (implicit TLS) |
| Authentication | Optional — see [Access Control](../configuration/access-control.html) |
| From address | A mailbox or alias in your Microsoft 365 tenant |

> [!WARNING]
> The **From** address must resolve to a real Microsoft 365 mailbox (or one of its aliases).
> Mail from an address Microsoft 365 does not own is rejected at send time.

## Step 5 — Send a test message and verify

Send one message from your application, then open the **Messages** page to watch it move from the
queue to *sent*. If something fails, the **Logs** page records the concrete reason.

> [!CAUTION]
> Users hosted on-premises in a hybrid tenant (no Exchange Online mailbox) pass sender validation
> but fail delivery with `MailboxNotEnabledForRESTAPI`. This is a known Microsoft 365 limitation —
> see [Troubleshooting](../reference/troubleshooting.html).

## What's next

- Lock down who may submit mail with [Access Control](../configuration/access-control.html).
- Turn on the daily report and admin alerts in [Notifications](../configuration/notifications.html).
- Review delivery statistics on the [Metrics](../monitoring/metrics.html) page.
