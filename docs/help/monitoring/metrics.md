# Metrics

This is the **Metrics** page. It shows delivery statistics and resource usage recorded over time, so
you can see trends and spot problems building up. The data comes from the local statistics database
(SQLite) and is the same data the scheduled [report](../configuration/notifications.html) is built
from.

> [!NOTE]
> This page is **read-only**. What is recorded (and for how long) is controlled by *Metrics Storage*
> and *Performance Metrics* on the [Monitoring](../configuration/monitoring.html) page.

## Email statistics (30 days)

| Card | Shows |
|---|---|
| Total Sent (30 days) | Messages delivered in the last 30 days. |
| Total Failed (30 days) | Delivery failures in the last 30 days. |
| Avg. Delivery (ms) | Average time to hand a message to Microsoft 365. |
| Unique Senders | Distinct From addresses seen. |

## Performance

The latest sampled **Memory**, **CPU**, and **Disk Free** values, plus trend charts you can view over
**24h / 7d / 30d**.

> [!NOTE]
> If the charts are empty and you see *“No performance data yet,”* performance sampling is turned
> off. Enable **Performance Metrics** on the [Monitoring](../configuration/monitoring.html)
> page to start recording memory, CPU and disk usage.

## Recent Activity

A table of recent per-message events — timestamp, event type, From, To, subject, size, delivery
duration, and a detail/error column — with a **↺ Refresh** button.

> [!TIP]
> Recent Activity is the fastest way to confirm a specific message went through and how long it took.
> For the actual message file (headers, body, attachments) use the [Messages](messages.html) page;
> for the underlying service events use the [Logs](logs.html) page.

> [!NOTE]
> If you see *“No metrics data available,”* the service has not processed any messages yet, or
> metrics recording is disabled on the Monitoring page.

## Related

- [Monitoring](../configuration/monitoring.html) — enable/tune what is recorded and retention
- [Messages](messages.html) — the actual queued / sent / failed message files
- [Notifications](../configuration/notifications.html) — the scheduled report built from this data
