# Backup & Restore

This is the **Backup & Restore** page of the Configuration Tool. It creates **encrypted, portable
backups of your configuration** — including the secrets — so you can recover after a failure or
move GraphMailer to another machine.

A backup is protected by its own password and is **not tied to the machine's encryption key**, so
it can be restored anywhere.

> [!NOTE]
> Changes on this page apply to the running service **without a restart**.

## Automatic Backups

Have the service write a backup on a schedule.

| Setting | Default | Range | Meaning |
|---|---|---|---|
| Enable scheduled backups | Off | — | Master switch. |
| Frequency | Weekly | Daily / Weekly | How often a backup runs. |
| Day of week | Sunday | — | Used for Weekly. |
| Time of day | `03:00` | `HH:mm` | 24-hour, service local time. |
| Keep last N backups | `14` | 1–365 | Older backups beyond this count are deleted (rotation). |
| Backup directory | `%ProgramData%\GraphMailer\backups` | — | Where backups are written; change with **Browse…**. |

## Backup Password

> [!CAUTION]
> This is a **separate password** that encrypts the backup file — **not** your Microsoft 365 client
> secret. It is required for both scheduled and manual backups. **Store it somewhere safe: without
> it, a backup cannot be restored.** There is no recovery if it is lost.

Enter and confirm the password here. It is stored encrypted (`ENC[…]`) so the service can run
unattended backups, but you must remember it independently to restore on another machine.

An **enabled schedule without a password shows a validation error**: the service would pause
scheduled backups in that state and no backup would ever be created. Either set a password or
disable the schedule.

## Email Backups

Optionally email each scheduled backup to a list of recipients (separate from the admin recipients).

> [!IMPORTANT]
> Emailed backups are sent **from the sender address configured on the
> [Notifications](notifications.html) page** — there is no separate sender here and no fallback
> account. If email backups are enabled without that sender, the page shows a validation error
> and the configuration cannot be saved.

| Setting | Default | Meaning |
|---|---|---|
| Email backups | Off | Send each new scheduled backup as an email attachment. |
| Recipients | — | Who receives the emailed backups. **Required** when email backups are on — enabling the toggle with an empty list shows a validation error, because the service would silently skip the email step. |

> [!WARNING]
> Emailing a backup sends your full encrypted configuration — secrets included — through Microsoft
> 365 to those recipients. The backup password still protects it, but treat the recipient list and
> the password with the same care as the secrets themselves.

## Manual Backup & Restore

- **Create backup now** — writes a backup immediately using the current settings and backup
  password.
- **Restore from file…** — pick a backup file and restore the configuration from it. You will need
  the password that backup was created with.

> [!IMPORTANT]
> Restoring **replaces** the current configuration. After a restore, review the settings and
> restart the service if any restart-required settings (listeners, TLS certificate, mail directory,
> polling interval) changed.

> [!NOTE]
> Separately from these backups, GraphMailer automatically keeps a copy of the previous config file
> whenever it migrates the configuration to a new schema version (under `config\backups\`). That is
> an internal safety net for upgrades — the encrypted backups on this page are your portable,
> restorable copies.

## Related

- [Notifications](notifications.html) — sender address and the "backup result" alert
- [Entra / Graph Setup](../getting-started/entra-setup.html) — what the encrypted secrets protect
