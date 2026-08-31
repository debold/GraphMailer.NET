# Message Rules

This is the **Message Rules** page of the Configuration Tool. It holds an ordered list of rules
that inspect every incoming message and can change or refuse it before it is queued.

> [!NOTE]
> Changes on this page apply to the running service **without a restart**.

## When rules run

Rules are evaluated **while the message is being received**, during the SMTP session, after the
malware scan and before anything is written to the queue.

That timing is what makes rules useful:

- A refusal reaches the **sending application** as a real SMTP reply, with the code and text you
  configured. The application can log it, alert on it, or hold the message — none of which is
  possible with a delivery report that arrives hours later.
- The queued message, the copy in `mail\sent\` and the statistics all show the **same** thing:
  the message as it will actually be delivered. There is no gap between what was received and
  what goes out.
- A retry re-sends what the rules produced, not the original — the rules are not re-applied and
  cannot be applied twice.

Rules run **after** the malware scan and never before it. The scanner has to see the message
exactly as the client sent it; a rule that removes attachments would otherwise delete the very
part the scan is meant to catch. A malware finding therefore always wins — see
[Malware Scan](malware-scan.md).

## Message Rules

The switch at the top turns the whole rule engine on or off. It is **off** after an upgrade, so
an update never starts changing mail that flowed the day before.

Below it you see how many rules exist, how many are active, and how many are in Enforce mode. If
any active rule is in Enforce mode a notice appears — those rules change or refuse live mail.

A red panel lists rules that cannot work as written: an invalid pattern, a condition combination
that can never match, an action missing a required value. These are worth taking seriously,
because the symptom at runtime is *nothing at all* — the rule sits in the list and never has an
effect.

## Rules

The grid is the rule list, **in evaluation order**. Rules run from top to bottom, so a rule
further down sees the message as the rules above it left it.

| Column | Meaning |
|---|---|
| **#** | Position, which is the evaluation order |
| **On** | A disabled rule is skipped entirely |
| **Name** | Identifies the rule in the log and in the statistics |
| **Mode** | `Audit` or `Enforce` — see below |
| **When** | How the conditions are combined, or "every message" when there are none |
| **Actions** | What the rule does, in order |
| **Notes** | ⚠ an action may not have the effect it sounds like · ■ no rule below this one runs for a matching message |
| **Hits (30 d)** | How often the rule has fired in the last 30 days |

Use **▲** and **▼** to reorder, **✎** to edit, **✕** to delete. **Duplicate** copies the selected
rule as a disabled Audit-mode variant, which is how you try out a change without touching the
original.

**↺ Refresh hits** re-reads the hit counts. A rule showing **—** has never fired: it is either
matching nothing or doing nothing. **Test rules…** says which.

### Audit and Enforce

A new rule starts in **Audit** mode. An audit rule logs and counts exactly what it *would* have
done and changes nothing. This is the safe way to introduce a rule: let it run against real mail
for a while, look at the log and the statistics, then switch it to **Enforce**.

Audit follows the same path enforcement would. It honours *stop after this rule*, and it stops
where a rejection or a discard would have happened. That is deliberate: if audit ignored those,
switching a rule to Enforce could change which *other* rules ran, and the audit would have
predicted the wrong outcome.

The one thing audit cannot simulate is the message itself. An audit rule that would reject lets
the message through **and** prevents the rules below it from running, so the message is delivered
without their changes.

## Conditions

A rule applies when its conditions match. With **no** conditions it applies to every message —
that is a deliberate "apply to all", not an oversight.

The rule dialog combines conditions with *all of them* or *any of them*, and each condition can
be inverted on its own.

### What a condition can look at

| Group | Fields |
|---|---|
| Envelope | Envelope sender, envelope recipient, recipient count |
| Headers | From, To, Cc, Reply-To, subject, any named header |
| Content | Plain-text body, HTML body |
| Attachments | Name, extension, size, count |
| Message | Size, importance, whether it is signed or encrypted |
| Session | Client IP, listener port, authenticated user, authenticated, TLS |

The **envelope** sender and recipients are what the sending application announced with `MAIL
FROM` and `RCPT TO`. They are not always the same as the `From:` and `To:` headers, and they are
what decides delivery.

An **attachment extension** matches with or without the leading dot — `xml` and `.xml` both work,
as they do in the *Remove attachments* action.

### How a condition compares

| Comparison | Notes |
|---|---|
| Equals, Contains, Starts with, Ends with | Case-insensitive unless you tick the box |
| Matches | Wildcards `*` and `?`; separate alternatives with `;` |
| Regular expression | .NET syntax; runs under a time limit (see *Limits*) |
| Domain is | Exact domain, e.g. `@example.com`. **Subdomains do not match** |
| In IP range | An IP or CIDR range, e.g. `10.20.0.0/16`; several separated by `;` |
| Greater than, Less than | Numeric fields only |
| Exists, Is empty | Asks about the field itself, not about a value |
| Is true | For the yes/no fields (TLS, authenticated, signed, encrypted) |

Only the comparisons that make sense for the selected field are offered.

### Fields that hold several values

Recipients, headers and attachments can hold more than one value. Such a condition is true when
**any** of them matches — and inverting it therefore means **none** of them match.

That is the useful reading. "No recipient outside the company" is written as *envelope recipient
— domain is — `@yourcompany.com`* with the condition inverted. Read the other way round it would
mean "at least one recipient is elsewhere", which is a completely different policy.

## Actions

Actions run in the order they are listed, and the order can be changed in the rule dialog.
**Refuse** and **Discard** end the rule — anything listed below them never runs.

| Action | What it does |
|---|---|
| Refuse the message | Rejects it during the SMTP session with your reply code and text |
| Discard silently | Accepts it with `250` and throws it away |
| Add recipient | Adds an address to To, Cc or Bcc |
| Remove recipient | Removes matching addresses |
| Replace recipient | Removes one address and puts another in its place |
| Set / prefix / suffix subject | Replaces the subject, or wraps the existing one |
| Put text above / below the body | Adds a banner or a disclaimer |
| Set / add / remove header | Sets, appends or removes a header |
| Remove attachments | By file name pattern, extension, or minimum size |
| Set importance | Low, Normal or High |
| Set From / Set Reply-To | Rewrites the sender or the reply address |

### Addresses

Actions that add or set **one** address — *Add recipient*, the new address of *Replace
recipient*, *Set From*, *Set Reply-To* — take a full address such as `user@example.com`. This is
the same rule the Notifications, Backup and SMTP user pages apply, so an address one page accepts
is an address every page accepts.

The *address to match* in *Remove recipient* and *Replace recipient* is a **pattern**, and accepts
more:

| Pattern | Matches |
|---|---|
| `user@example.com` | that exact address |
| `@example.com` | every address at exactly that domain (not subdomains) |
| `*@example.com` | wildcard — `*` for any text, `?` for one character |
| `*` | every recipient |

Separate several patterns with `;`.

### Spaces are kept

*Prefix subject*, *Suffix subject*, *Set subject* and the two body actions store your text exactly
as typed, including leading and trailing spaces and blank lines.

That is deliberate: a prefix of `[EXTERNAL] ` needs its trailing space, or the subject arrives as
`[EXTERNAL]Quarterly report`. Everywhere else — addresses, header names, patterns — surrounding
spaces are removed, because there they are only a typo.

## What reaches the recipient

GraphMailer does not hand raw message data to Microsoft 365 — Graph rebuilds the message property
by property. A few consequences are worth knowing before writing a rule.

### Recipients follow the envelope

Delivery is decided by the envelope, not by the `To:` and `Cc:` headers. Every recipient action
therefore changes **both**.

This matters most for removal. Taking a recipient out of the header alone would leave them in the
envelope, and they would still receive the message — as an invisible blind copy. Adding a **Bcc**
recipient works the other way round: the address goes into the envelope only, because a `Bcc:`
header would be ignored on delivery *and* would put the blind copy into the archived message.

### Only `x-…` headers are carried

Microsoft 365 relays custom headers only when their name starts with `x-`, and at most **five**
per message. Everything else — `List-Unsubscribe`, `Auto-Submitted`, your own `Origin:` — is set
in the message GraphMailer stores but is **not** delivered. The action dialog says so while you
configure it.

Going over five is worse than losing the new header: Microsoft 365 rejects such a message, and
the automatic retry then drops **every** custom header and the sender name. The count depends on
what the incoming message already carries, so it cannot be checked while you configure the rule —
the service warns in the log when a message ends up over the limit.

`x-ms-exchange…` headers are reserved by Exchange and always dropped. `X-Priority` is not relayed
as a header either — use the *Set importance* action instead.

### Only one body version is delivered

A message can carry a plain-text body and an HTML body. When it carries both, **only the HTML one
is delivered**.

Body actions therefore take a text version and an optional HTML version. If you leave the HTML
empty it is generated from the text (escaped, with line breaks preserved), which is enough for a
plain banner. Write the HTML yourself when you want formatting or a link.

A body version the message does not have is **never created**. Adding an HTML body to a
plain-text message would change how every recipient sees it — far more than the rule asked for.
If a message has only plain text, the HTML version of your banner is simply not used.

### Signed and encrypted messages

Body and attachment actions are **skipped** on signed and encrypted messages, and the reason is
written to the log. The signature covers exactly that content.

Everything else still applies — refusing, recipients, subject, headers, importance — because
those sit outside the signature and the encryption.

### Rewriting the sender

*Set From* changes the address **and** the mailbox the message is sent from. The allowed and
blocked sender lists are checked again afterwards, because they ran at `MAIL FROM` against the
original address; a rule cannot quietly get around them.

The tenant sender check is **not** repeated — it is a lookup against Microsoft 365 and this
happens in the middle of an SMTP session. A rewritten sender the directory does not know will
fail at delivery instead of here.

*Set Reply-To* only sets the reply address and is uncritical.

## Rule Tester

**Test rules…** below the rule list opens the tester in its own window. It runs the rules
**exactly as they stand on this page**, saved or not, against a message you describe or an `.eml`
file you load. It uses the service's own rule engine, so what it reports is what the relay would
do.

Nothing is sent, queued or written to disk.

The window is laid out like a mail client: the message properties on top, the message fields on
the left with the transport settings beside them, and the content in tabs below. Fill in what your
conditions look at and press **Run test** — the **Result** tab comes forward with the answer.

**To**, **Cc** and **Bcc** are separate boxes, one address per line, and they behave the way they
do in delivery: To and Cc go into their headers *and* the envelope, while Bcc goes into the
envelope only — a blind copy appears in no header, which is exactly what makes it blind. The
envelope the rules see is all three together.

The divider between the fields and the tabs can be dragged to give either half more room, and the
window fits itself to the screen; both halves scroll on their own when space is tight.

### Message properties

**Importance** and **Protection** sit at the top because they are properties of the whole message
rather than fields in it.

*Signed* and *Encrypted* build a message with that structure, which is the only way to try out a
rule about protected mail without finding a genuinely signed message first. A message is one or
the other, never both. Remember that body and attachment actions are **skipped** on protected mail
— that is exactly what these switches let you observe.

### Content tabs

| Tab | What it is for |
|---|---|
| **Text body** | The plain-text rendering |
| **HTML body** | The HTML rendering — leave it empty for a plain-text message |
| **Headers** | One header per line, as `X-Origin: erp` |
| **Result** | What the rules did. Selected automatically when you run a test |

The Headers tab is what makes a *Header* condition testable at all. Note that only headers named
`x-…` reach the recipient (see [What reaches the recipient](#what-reaches-the-recipient)); one
named otherwise is still set on the message and can still be matched by a rule.

### Attachments

One per line, either as a name or as a name and a size:

```
report.pdf
macro.docm | 20480
```

The size is in bytes and is what a rule matching on attachment size compares against; a line
without one gets a small default. Leave the box empty to test a message with no attachments.

### Transport

Client IP, port, TLS and authentication sit in their own box beside the message fields, because
**none of them is part of the message** — they describe how it reached the relay.

**Port** offers the listeners configured on the [Servers & TLS](servers-tls.md) page, including
ones you have added but not yet saved. A port no listener uses cannot occur in practice, so it is
not offered.

### The result

The result shows:

- the **verdict**: queued, refused (with the exact SMTP reply), or discarded;
- **every rule**, and what became of it — see below;
- warnings, including anything that was skipped;
- **changes** — only the fields the rules actually altered, each with its old and new value. When
  nothing changed it says so, rather than printing the message twice and leaving you to compare;
- which body version Microsoft 365 will deliver.

If a header names someone the envelope does not, the tester adds a *Not delivered to* line: that
address looks like a recipient in the message and is not one.

### Why a rule did nothing

The rule list in the result covers **every** rule, not only the ones that fired — that is what
answers "my rule is configured and nothing happens":

| Marker | Meaning |
|---|---|
| **✓** | The rule matched. Each action is listed with what it did, or would have done in Audit mode. |
| **✗** | The rule did not match, and the line below names **the condition that failed**. |
| **–** switched off | The rule is disabled, so it was never evaluated. |
| **–** not reached | An earlier rule ended the run before this one — check the ■ marker in the rule list. |

A ✗ with a named condition is usually the answer: the rule is fine, but the message does not
carry what the condition asks for. Compare the condition with what the *Before* block reports —
for an attachment rule, whether the attachment is really there and really has that extension.

### Testing a real message

**Load .eml…** fills every field from a real message — sender, recipients, subject, both bodies
and the attachments with their sizes — and tests against **the file itself**, not the form. That
keeps the original structure: inline images, nested messages, a signature, anything a form cannot
express. Running the test again gives the same answer.

The service stores every message as a pair: `{id}.eml` next to `{id}.meta.json` under
`mail\queue`, `mail\sent` or `mail\failed` (see [Messages](../monitoring/messages.md)). Pick
either file — the other is found automatically — and the metadata fills in what the message alone
cannot say:

| From the message | From the metadata file |
|---|---|
| Subject, both bodies, attachments | **Envelope sender and recipients**, client IP |

That distinction matters. The `From:` and `To:` headers are not the SMTP envelope, and the
envelope is what decides delivery: a blind copy appears in no header at all, and a `To:` header
can name an address the sender never issued `RCPT TO` for. With the metadata file the test runs
against the recipients the rules really saw.

Listener port, TLS and authentication are not stored with a message — set those yourself.

Half a pair is fine. A message without its metadata falls back to the headers and says so; a
metadata file whose message is gone still fills in the envelope, client IP and subject, and leaves
the body and attachments for you to describe.

While a message is loaded, **every field whose value came from a file is locked** — shown greyed,
still selectable so you can copy from it. That is the whole rule, and it explains the transport
group: the client IP *is* stored in the metadata file, so it locks with the rest; the port, TLS
and authentication are stored nowhere, so they always stay editable. Varying those against a fixed
message is exactly what you want ("does this rule fire on that port, over TLS?").

**Edit as form** keeps the content and unlocks the fields. From that point the message is rebuilt
from what the fields say, so anything the file carried beyond them is no longer part of the test.
This is the way to take a real message and change one thing about it without retyping the rest.

**Treat Audit rules as Enforce** previews what would happen after you switch the rules on, without
switching them on.

## Limits and Records

### Body examined (kB)

How much of the message body the content conditions look at. Default **1024 kB**.

A longer body is examined up to this point, not skipped. That keeps the behaviour predictable: a
condition looking for text near the end of a very long message will not find it, but an inverted
condition will not suddenly fire on every large message either.

### Pattern time limit (ms)

How long a regular expression may run against one value. Default **100 ms**.

Patterns are written by you but run against content sent by others, and some patterns take
disproportionately long on certain input. A pattern that exceeds the limit counts as **no match**
and is logged, so a costly expression cannot hold up mail delivery.

### Keep records for (days)

How long the record of a discarded message is kept. Default **60 days**.

This is deliberately separate from the retention setting on the Malware Scan page, which also
applies when scanning is switched off entirely.

### Also store the full message when a rule discards it

Off by default.

A discarded message leaves no other trace: the sending application was told it was accepted, and
nothing was queued. Without the message itself, a silent drop is very hard to investigate. Storing
every discarded message is a deliberate decision, so it is opt-in.

Records live under `mail\blocked\`, alongside the malware findings but marked as their own kind —
neither list shows the other's entries.

## When a rule cannot work

The rule engine never takes mail down for a policy problem:

- An invalid pattern, an impossible condition or an unexpected error is reported, and the message
  is relayed **unmodified**.
- A message the parser cannot make sense of is relayed as it arrived; no rule runs on it.
- A rule that removes every recipient **discards** the message instead of queueing it. Queueing a
  message with nobody to deliver to would only produce a delivery failure and a delivery report to
  a sender who did nothing wrong.
- If a rule pushes a message over the recipient limit set on the [Servers & TLS](servers-tls.md)
  page, it is refused during the SMTP session rather than failing at delivery hours later.

## Where rule activity shows up

- **Log** — every applied rule at Information level, naming the rule and what it did; audit rules
  say what they would have done. Skipped actions and unusable rules are warnings.
- **[Metrics](../monitoring/metrics.md)** — a *Message Rules* card counts hits per rule and per
  mode. A refusal also appears in the rejection breakdown as *Refused by a message rule*. A
  discard does not: the sending application was told the message was accepted, so nothing was
  refused.
- **The rule grid** — hits over the last 30 days, so a rule that never fires is visible where it
  is edited.

> [!NOTE]
> A discarded message is not counted as received, for the same reason a message rejected by the
> malware scan is not: it never entered the relay's mail flow.
