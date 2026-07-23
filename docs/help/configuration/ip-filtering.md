# IP Filtering

This is the **IP Filtering** page of the Configuration Tool. It controls **which network addresses**
may relay mail through GraphMailer, and automatically blocks IPs that misbehave. For an internal
relay this is the first line of defence — especially for plaintext listeners that have no
authentication.

> [!NOTE]
> Changes on this page apply to the running service **without a restart**.

## IP Whitelist

A list of allowed IP addresses or CIDR ranges, each with an optional comment.

- When the whitelist is **not empty**, **only** listed addresses may send mail — every other source
  is rejected at **MAIL FROM**.
- A fresh install seeds the whitelist with the private/internal address space, so internal apps
  work out of the box and the public internet does not:

| Entry | Comment |
|---|---|
| `10.0.0.0/8` | Private network (RFC 1918) |
| `172.16.0.0/12` | Private network (RFC 1918) |
| `192.168.0.0/16` | Private network (RFC 1918) |
| `127.0.0.0/8` | IPv4 loopback |
| `::1/128` | IPv6 loopback |
| `fc00::/7` | IPv6 unique-local (RFC 4193) |
| `fe80::/10` | IPv6 link-local |

Add the specific IP of each relaying application, or keep the broad private ranges if you trust
your whole internal network.

> [!WARNING]
> Removing **all** whitelist entries means *no IP restriction at all* — any source that can reach
> the port may attempt to relay. Keep the whitelist populated unless another control (authentication
> on every listener) fully covers access.

## IP Blacklist

IP addresses or CIDR ranges that are **always rejected** at MAIL FROM, regardless of the whitelist.
Use it to stop a specific problem source.

## Automatic IP Blocking

GraphMailer counts failed events (failed authentication, filter rejections) per source IP and
temporarily blocks an address that crosses a threshold — basic brute-force / abuse protection.

| Setting | Default | Range | Meaning |
|---|---|---|---|
| Max failures | `10` | 1–100 | Failures from one IP within the window before it is blocked. |
| Within (minutes) | `10` | 1–1440 | Sliding window in which failures are counted; resets if the threshold is not reached. |
| Block duration (minutes) | `10` | 1–1440 | How long the IP stays blocked; all its connections are refused until it expires. |

So the default reads: **10 failures within 10 minutes → blocked for 10 minutes.**

> [!NOTE]
> The block is enforced from **MAIL FROM** onward, and the failure counter is kept per unique IP in
> memory. Blocks do not survive a service restart.

## Currently Blocked IPs

A live view of addresses blocked **right now**, with the failure count, when the block started, and
when it expires. Use **↺ Refresh** to update, and **Unblock** to release an address immediately —
handy if a legitimate client tripped the limit (for example after a misconfigured password).

> [!TIP]
> If a real application keeps getting blocked, check the [Logs](../monitoring/logs.md) for *why*
> its attempts fail (wrong password, not whitelisted, rejected sender) and fix that cause, rather
> than only unblocking it repeatedly.

## Related

- [Servers & TLS](servers-tls.md) — listener ports and whether they require authentication
- [Access Control](access-control.md) — SMTP users and sender/recipient lists
- [Logs](../monitoring/logs.md) — the reason behind each rejection or block
