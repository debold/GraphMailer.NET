# GraphMailer

**A Windows SMTP relay that delivers mail through Microsoft 365 via the Graph API.**

Legacy applications, scanners, and appliances that can only speak SMTP keep working —
GraphMailer accepts their mail on a local SMTP port and hands it to Exchange Online
through the Microsoft Graph API, using modern app-only authentication instead of
deprecated Basic Auth / SMTP AUTH.

## Features

- **Runs as a Windows service** — install once, survives reboots, no console window.
- **Standard SMTP listeners** on ports 25, 465 (implicit TLS), and 587 (STARTTLS),
  freely configurable. TLS certificates are picked straight from the Windows
  certificate store — renewal-safe, no PFX juggling.
- **Access control**: SMTP authentication with per-user sender restrictions,
  IP whitelist/blacklist, and automatic blocking of brute-force clients.
- **Reliable delivery**: incoming mail is persisted to a disk queue first, then sent
  via Graph with an Exchange-aligned retry schedule. Internet or Graph outages never
  lose mail; permanently rejected messages produce a non-delivery report (NDR).
- **Large attachments** are handled automatically via Graph upload sessions.
- **Graphical ConfigTool** (no config files to hand-edit): guided **Entra ID setup
  wizard** that registers the app and permissions for you, live service status,
  delivery statistics, message tracking, and log viewer.
- **Operations reports by email** (daily/weekly/monthly) plus admin notifications
  for failures, certificate expiry, and disk space.
- **Secrets stay encrypted** — passwords and client secrets are stored encrypted at
  rest (Windows Data Protection), never in plaintext.
- **MSI installer** with a bootstrapper that brings the .NET 10 runtime along;
  bundled offline HTML help for every screen.

## Requirements

- Windows x64 (server or client), Windows Server 2016+ recommended
- Microsoft 365 tenant with Exchange Online mailboxes
- .NET 10 Desktop Runtime (installed automatically by the setup bundle)

## Getting started

1. Download `GraphMailerSetup-<version>.exe` from the
   [Releases](../../releases) page and run it.
2. Open the **GraphMailer ConfigTool** from the Start menu and run the
   **Entra ID setup wizard** (Graph API page) — it registers the app in your tenant
   and grants the required permissions (`Mail.Send`, `Mail.ReadWrite`, `User.Read.All`).
3. Configure listeners, allowed senders, and SMTP users to match your environment,
   then point your applications at the relay.

The bundled help (Start menu → *GraphMailer Help*) walks through every page,
including a quickstart and troubleshooting guide. You can also read the same help
online before installing — it is browsable right here on GitHub:
**[GraphMailer Help](docs/help/index.md)** (installation, Entra setup, a reference
page for every ConfigTool screen, troubleshooting, FAQ, and glossary).

## Silent install

```
GraphMailerSetup-<version>.exe /quiet /norestart
```

## Changelog

Every user-visible change is recorded in **[CHANGELOG.md](CHANGELOG.md)** — new features, fixes,
and behavioural changes, newest first.

## Testing

GraphMailer ships with an extensive automated test suite — unit tests, SMTP/HTTP
integration tests, and opt-in live tests against a real Microsoft 365 tenant. Every
single test is catalogued, with its scenario and expected result, in
**[docs/TEST_DOCUMENTATION.md](docs/TEST_DOCUMENTATION.md)** — a useful map of exactly
which behaviours (security decisions, retry logic, config schema, delivery paths) are
covered.

## License

[MIT](LICENSE)
