# Security Policy

GraphMailer accepts mail from applications on your network and sends it on with Microsoft 365
credentials. Security problems in it matter, and reports are welcome.

## Reporting a vulnerability

**Please do not open a public issue for a security problem.**

Report it privately through GitHub instead:
[Report a vulnerability](https://github.com/debold/GraphMailer.NET/security/advisories/new)
(also reachable from the repository's **Security** tab → *Report a vulnerability*).

A useful report contains:

- the GraphMailer version (ConfigTool → *Status* → *Software Update*, or the file version of
  `GraphMailer.exe`)
- the Windows version
- what an attacker can do, and from where (local machine, network, mail content)
- steps to reproduce, or a proof of concept
- the relevant configuration, with secrets removed

**Do not include real tenant data**: no tenant or client IDs, client secrets, certificates,
passwords, mailbox addresses or mail content from a production system. Use placeholders or a
test tenant.

You will get an answer within 14 days. GraphMailer is maintained by one person in their own
time, so a fix may take longer than that, but you will hear what happens next. Once a fix is
released, the advisory is published, and you are credited unless you prefer not to be.

## Supported versions

Only the **latest release** receives security fixes. Fixes are not backported to older
versions; the way to get a fix is to install the newest release over the existing installation,
which keeps the configuration, secrets and queued mail.

The weekly update check (off by default; turn it on under ConfigTool → *Monitoring*) shows on
the *Status* page when a newer release is available, and can also send an email alert.

## Scope

Particularly relevant are problems in:

- **SMTP access control**: authentication, the IP allow list and IP blocking, relay
  restrictions (who may send as whom)
- **Secret handling**: the `ENC[...]` values in `graphmailer.json`, the Data Protection key ring
  in the registry, and the Graph client secret or certificate
- **TLS**: listener certificate selection and protocol versions
- **Mail processing**: parsing of untrusted mail content, attachments and headers, and the
  files written to the mail queue
- **The installer and the Windows service**: file and registry permissions, the service account

Out of scope:

- Weaknesses that require an attacker who is already an administrator on the GraphMailer server
- Behaviour of Microsoft 365, Exchange Online or Entra ID itself
- Deliberately accepted trade-offs that are documented as such, for example the plain-SMTP
  fallback when the TLS certificate is missing (it logs an error, and `Certificate.FailClosed`
  turns it off)
