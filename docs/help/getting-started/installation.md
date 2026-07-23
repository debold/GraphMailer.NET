# Installation

GraphMailer ships as a single Windows installer that sets up two programs:

- the **GraphMailer service** — the background Windows service that listens for SMTP and relays
  mail to Microsoft 365, and
- the **GraphMailer Configuration Tool** — the desktop app you use to configure and monitor it.

You only need to run one installer; both are installed together.

## Before you begin

- A **64-bit Windows** machine (Windows 10/11 or Windows Server) that stays on to relay mail.
- **Local administrator** rights — the installer registers a Windows service and a firewall rule.
- The **.NET 10 Desktop Runtime (x64)**. You do not have to install this yourself: the default
  installer detects it and installs it for you if it is missing (see below).

> [!IMPORTANT]
> **Upgrading from a version before 1.3?** Those releases ran on the .NET **8** Desktop Runtime.
> Use `GraphMailerSetup-<version>.exe` — it installs the .NET 10 runtime for you. The bare
> `.msi` refuses to install without it rather than leaving a service that cannot start.

> [!NOTE]
> GraphMailer does **not** need to run on a domain controller or mail server, and it does not
> open any inbound port to the internet. It only accepts SMTP from the machines you allow on the
> [IP Filtering](../configuration/ip-filtering.md) page.

## Choosing an installer

The release contains more than one file. Pick the one that matches your situation:

| File | Use this when… |
|---|---|
| `GraphMailerSetup-<version>.exe` | **Default.** Installs the .NET runtime automatically (downloads it on demand if the machine does not already have it). |
| `GraphMailerSetup-<version>.exe` shipped next to `windowsdesktop-runtime-*.exe` | The target machine has **no internet access** — the runtime is bundled alongside and installed offline. |
| `GraphMailer-<version>.msi` | The **.NET 10 Desktop Runtime (x64) is already installed** and you want just the app, with no runtime handling. If it is missing, the MSI stops with a message instead of installing. |

## Interactive install

1. Double-click `GraphMailerSetup-<version>.exe`.
2. Approve the Windows **User Account Control** prompt (administrator rights).
3. Follow the wizard. If the .NET runtime is missing it is installed first, automatically.
4. On the final screen, leave **Launch** ticked to open the Configuration Tool right away.

That's it — the service is installed, started, and set to start automatically with Windows.

## What the installer sets up

| Item | Detail |
|---|---|
| Programs | Installed to `C:\Program Files\GraphMailer\` |
| Windows service | Service name **`GraphMailer`**, runs as **LocalSystem**, **automatic** start, restarts automatically after a crash (60 s delay, up to three times per day) |
| Start menu | A shortcut to the (elevated) Configuration Tool |
| Firewall | An inbound rule bound to `GraphMailer.exe` so your configured SMTP ports are reachable — no manual port rule needed |
| Runtime data | Created by the apps under `C:\ProgramData\GraphMailer\` (config, logs, mail queue, metrics) |

> [!TIP]
> Program files and runtime data are deliberately separate. Your configuration, logs and queued
> mail live under `C:\ProgramData\GraphMailer\` and are **not** removed when you uninstall the
> program, so an upgrade never loses your settings.

## Silent / unattended install

For deployment via a management tool, install without any prompts:

```powershell
# Installs the runtime prerequisite if needed, then GraphMailer
GraphMailerSetup-<version>.exe /quiet /norestart

# Progress bar, but no prompts
GraphMailerSetup-<version>.exe /passive /norestart

# Write an install log
GraphMailerSetup-<version>.exe /quiet /norestart /log C:\Temp\graphmailer-install.log
```

Uninstall silently with:

```powershell
GraphMailerSetup-<version>.exe /uninstall /quiet /norestart
```

## Upgrading

Installing a newer version automatically removes the previous one (a *major upgrade*). The
service is stopped during the upgrade and restarted afterwards. Your configuration and data under
`C:\ProgramData\GraphMailer\` are preserved.

## Uninstalling

Uninstall from **Settings → Apps → Installed apps**, or run the silent command above. The Windows
service and firewall rule are removed.

> [!WARNING]
> Uninstalling leaves your data under `C:\ProgramData\GraphMailer\` in place (config, logs, queued
> and archived mail). Delete that folder manually only if you are sure you no longer need it — any
> mail still sitting in the queue would be lost.

## Next step

The service is now running on safe defaults, but it cannot send to Microsoft 365 yet. Continue
with the **[Quickstart](quickstart.md)**, then connect your tenant in
**[Entra / Graph Setup](entra-setup.md)**.
