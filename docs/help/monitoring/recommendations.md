# Recommendations

This is the **Recommendations** page — a short list of settings that would make this installation
more secure, more reliable, or easier to operate. GraphMailer checks your configuration against a
fixed catalogue of suggestions and lists the ones that apply.

> [!NOTE]
> These are suggestions, **not** problems. Nothing on this page affects the service's health status,
> blocks a save, or stops mail from flowing. An installation that shows several tips is working
> perfectly well — the tips only point at features you have not switched on.

When there is something to suggest, the sidebar shows a count next to **Recommendations**:

```
OVERVIEW
  📊 Status
  💡 Recommendations  ③
```

The count is the number of **open** suggestions only — handled and hidden ones are not something
to act on.

## The three sections

The page is split into three collapsible sections:

| Section | Contains | Default |
|---|---|---|
| **Open** | Suggestions that apply and have not been acted on. | Expanded |
| **Done** | Suggestions this installation already satisfies. | Collapsed |
| **Hidden** | Suggestions you hid, whether or not they still apply. | Collapsed |

Nothing disappears when you fix a setting — it moves from **Open** to **Done**, so the page stays a
record of everything that was ever suggested rather than a list that quietly shrinks. A handled
suggestion keeps its title but shows a short confirmation line instead of the argument for it.

A section with nothing in it is not shown at all.

> [!NOTE]
> Suggestions that are not relevant to this installation appear in **no** section. If Graph is not
> set up yet, the two Graph-related tips are absent entirely rather than listed as done — GraphMailer
> has not checked anything it could call handled.

## What is checked

| Priority | Suggestion | Appears when | Fix it on |
|---|---|---|---|
| High | Enable TLS on the listeners that accept authentication | An enabled listener runs in plain mode **and** its auth mode is Optional or Required | [Servers & TLS](../configuration/servers-tls.html) |
| High | Authenticate to Graph with a certificate instead of a client secret | The Entra app uses a client secret and no certificate | [Graph API](../configuration/graph-api.html) |
| High | Switch on the alerts that warn you before mail stops | An early-warning alert is off while admin notifications are otherwise working | [Notifications](../configuration/notifications.html) |
| Medium | Turn on sender validation | Sender validation is off (and Graph is configured) | [Access Control](../configuration/access-control.html) |
| Medium | Enable automatic configuration backups | Scheduled backups are off | [Backup & Restore](../configuration/backup-restore.html) |
| Medium | Send non-delivery reports | NDRs are off | [Notifications](../configuration/notifications.html) |
| Medium | Add a recipient for admin notifications | No admin notification recipient is configured | [Notifications](../configuration/notifications.html) |
| Medium | Turn on the update check | The weekly GitHub release check is off | [Monitoring](../configuration/monitoring.html) |
| Low | Set the log level back to Information | The log level is anything other than *Information* | [Monitoring](../configuration/monitoring.html) |
| Low | Consider sharing anonymous usage telemetry | Anonymous telemetry is off | [Monitoring](../configuration/monitoring.html) |

> [!NOTE]
> The TLS suggestion is about **credentials**, not encryption in general. A plain listener whose
> auth mode is *None* never sees a password, so it is not flagged — the default port-25 relay
> listener is a normal, supported setup. It is a listener that accepts SMTP AUTH *and* runs
> unencrypted that puts passwords on the wire.

The **early-warning alerts** the third suggestion looks at are the ones that reach you while there
is still time to act:

- Graph client certificate expiring (only when Graph uses certificate authentication)
- Email delivery failed
- Graph API unreachable
- TLS listener certificate expiring
- Low disk space
- SMTP port connectivity failure

It names whichever of these are switched off. The remaining events (IP blocked, service
start/stop, backup result, new version available) report something already over and are a matter of
taste, so they are not part of it. The suggestion only appears once admin notifications are
switched on and have a recipient — before that, the *Add a recipient for admin notifications*
suggestion already covers the same ground.

## Priority

Every suggestion carries a priority, shown as a chip on its card, and the list is sorted by it:

| Priority | Means |
|---|---|
| **High** | Credentials or secrets are exposed, or a foreseeable outage is being set up. |
| **Medium** | A failure would go unnoticed, or recovery would be markedly harder. |
| **Low** | Hygiene and optional extras — worth doing, nothing breaks without it. |

Even *High* describes a risk you may knowingly accept — it is never a fault in the running service,
and it never changes the health status on the [Status](status.html) page.

Each open suggestion also carries a **Why it matters** paragraph: the concrete consequence of
leaving the setting as it is, kept separate from the description so you can weigh the change
without reading the whole card. Handled suggestions drop it — the argument is moot once it is done.

Suggestions that depend on a finished setup are left out until then: the two Graph-related tips only
appear once a tenant ID and client ID exist, and the TLS tip only once at least one listener is
enabled. A brand-new installation is therefore not flooded with advice about steps you have not
reached yet.

Within each section the tips are sorted by priority first and then by category — **Security**,
**Reliability**, **Operations**, **Product**. Each card shows its category next to the page that
fixes it.

## Acting on a tip

| Button | What it does |
|---|---|
| Go to setting | Opens the configuration page that holds the matching setting. |
| Hide | Moves the tip to **Hidden** and drops it from the report email. |
| Show again | Returns a hidden tip to **Open** or **Done**, depending on the current setting. |

> [!IMPORTANT]
> A tip moves from **Open** to **Done** when you **save** the setting that resolves it — not when
> you tick its checkbox. The page always describes the configuration as it is stored on disk, which
> is what the running service and the report email see too.

## Hiding tips

Not every suggestion fits every installation. A relay on an isolated network segment may have good
reasons to run plain SMTP; an environment with its own central backup does not need GraphMailer's.
**Hide** removes such a tip for good.

- Hiding takes effect immediately and is written straight to the configuration file — it does **not**
  wait for the Save button and does not create unsaved changes.
- A hidden tip is also dropped from the **Recommendations** box of the periodic
  [report email](../configuration/notifications.html).
- Hiding is sticky: if you later switch the feature on and off again, the tip stays hidden.
- A hidden tip stays in **Hidden** even once the setting satisfies it, so that section always shows
  exactly what you have hidden and remains the one place to review the list.
- Nothing is lost — open the **Hidden** section to see them, each with a **Show again** button.

The preference is stored in `graphmailer.json` under `Recommendations.Dismissed` as a list of short
identifiers, so it travels with a configuration backup and is restored along with everything else.

```json
"Recommendations": {
  "Dismissed": [ "telemetry", "log-level" ]
}
```

## Related

- [Status](status.html) — the health checks, which are about problems rather than suggestions
- [Notifications](../configuration/notifications.html) — the periodic report that repeats these tips
- [Monitoring](../configuration/monitoring.html) — log level, update check and telemetry settings
