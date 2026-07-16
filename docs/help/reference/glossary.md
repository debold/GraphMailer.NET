# Glossary

Terms used throughout this help and the Configuration Tool.

**Admin consent**
: An administrator's approval, on behalf of the whole organisation, for an app's *application*
permissions. GraphMailer's permissions cannot be used until consent is granted. See
[Entra / Graph Setup](../getting-started/entra-setup.html).

**Alias**
: An additional email address for a mailbox (a *proxy address*). GraphMailer resolves an alias to the
owning mailbox for sending.

**Application Access Policy**
: An Exchange Online policy (`New-ApplicationAccessPolicy`) that restricts which mailboxes an app may
access, so GraphMailer's tenant-wide mail permissions are limited to specific mailboxes.

**CIDR**
: A compact way to write an IP address range, e.g. `192.168.0.0/16`. Used in
[IP Filtering](../configuration/ip-filtering.html).

**Client ID (App ID)**
: The identifier of the application registered in Microsoft Entra ID. See
[Graph API](../configuration/graph-api.html).

**Client secret**
: A password-like string used to authenticate an app to Entra. An alternative to a certificate;
GraphMailer prefers a certificate. Stored encrypted.

**ENC[…]**
: How GraphMailer marks an **encrypted** value in its configuration file. Secrets (passwords, the
client secret, the backup password) are stored this way, never in plain text.

**Entra ID**
: Microsoft Entra ID, formerly Azure Active Directory (Azure AD) — the identity service where
GraphMailer is registered as an application.

**Graph API**
: Microsoft's modern REST API for Microsoft 365. GraphMailer uses it to send mail instead of talking
SMTP to Exchange Online.

**Implicit TLS**
: Encryption that starts from the first byte of the connection (classic port 465), as opposed to
STARTTLS. See [Servers & TLS](../configuration/servers-tls.html).

**Listener**
: One TCP port GraphMailer accepts SMTP connections on, with its own encryption and authentication
settings. See [Servers & TLS](../configuration/servers-tls.html).

**MAIL FROM**
: The SMTP command carrying the sender (envelope) address. Sender allow/block lists and validation
act here.

**NDR (Non-Delivery Report)**
: A bounce notification sent when an accepted message permanently fails to deliver. Configured on
[Notifications](../configuration/notifications.html).

**Object ID**
: Microsoft 365's internal identifier for a user/mailbox. Graph's send operation accepts a **UPN** or
object id; aliases are resolved to the owner's object id.

**RCPT TO**
: The SMTP command carrying a recipient address. Recipient allow/block lists act here.

**Relay**
: A server that accepts mail and forwards it on. GraphMailer is an SMTP relay into Microsoft 365.

**Service principal**
: The local representation, in your tenant, of a registered application — what permissions are
actually granted to. Created by the setup wizard.

**STARTTLS**
: An SMTP feature that upgrades a plain connection to an encrypted one (typically port 587). Can be
optional or required per listener.

**Tenant ID**
: The GUID identifying your Microsoft 365 / Entra organisation. See
[Graph API](../configuration/graph-api.html).

**Thumbprint**
: A certificate's unique fingerprint (SHA-1 hex). GraphMailer stores it to locate the exact
certificate in the Windows store. Preferred over subject-name selection for Entra authentication.

**UPN (User Principal Name)**
: A user's sign-in name in Microsoft 365, usually their primary email address. Accepted by Graph's
send operation.

## Related

- [FAQ](faq.html) · [Troubleshooting](troubleshooting.html) · [Quickstart](../getting-started/quickstart.html)
