using System.Net;
using System.Text;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services;
using MimeKit;

namespace GraphMailer.Service.Infrastructure.Rules;

/// <summary>What applying one action did.</summary>
/// <param name="Changed">The message itself was modified and has to be re-serialised.</param>
/// <param name="EnvelopeChanged">The envelope recipients or sender changed.</param>
/// <param name="Detail">What happened, for the log and the rule tester.</param>
/// <param name="Warning">Set when the action could not do what it was asked to.</param>
internal readonly record struct ActionEffect(
    bool Changed, bool EnvelopeChanged, string Detail, string? Warning = null)
{
    internal static ActionEffect None(string detail, string? warning = null)
        => new(false, false, detail, warning);
}

/// <summary>
/// The MimeKit mutations behind the action types. Static and free of configuration or IO —
/// everything it needs arrives in the <see cref="MessageRuleContext"/>.
///
/// Two rules run through all of this:
///   • Body edits target the parts <see cref="MimeMessageSplitter"/> picks, because those are
///     the ones <c>GraphApiClient.BuildMessage</c> actually delivers. Editing a different text
///     part would produce a banner nobody ever sees.
///   • Recipient edits touch the headers <i>and</i> the envelope. Delivery follows the envelope,
///     so a header-only change would move a recipient rather than remove them.
/// </summary>
internal static class MessageRuleActions
{
    /// <summary>Applies one action to the message and envelope in the context.</summary>
    internal static ActionEffect Apply(RuleAction action, MessageRuleContext ctx) => action.Type switch
    {
        RuleActionType.AddRecipient => AddRecipient(ctx, action),
        RuleActionType.RemoveRecipient => RemoveRecipient(ctx, action),
        RuleActionType.ReplaceRecipient => ReplaceRecipient(ctx, action),

        RuleActionType.SetSubject => SetSubject(ctx, action.Value ?? string.Empty),
        RuleActionType.PrefixSubject => SetSubject(ctx, (action.Value ?? string.Empty) + (ctx.Message.Subject ?? string.Empty)),
        RuleActionType.SuffixSubject => SetSubject(ctx, (ctx.Message.Subject ?? string.Empty) + (action.Value ?? string.Empty)),

        RuleActionType.PrependBody => ModifyBody(ctx, action, prepend: true),
        RuleActionType.AppendBody => ModifyBody(ctx, action, prepend: false),

        RuleActionType.SetHeader => SetHeader(ctx, action, replace: true),
        RuleActionType.AddHeader => SetHeader(ctx, action, replace: false),
        RuleActionType.RemoveHeader => RemoveHeader(ctx, action),

        RuleActionType.RemoveAttachments => RemoveAttachments(ctx, action),
        RuleActionType.SetImportance => SetImportance(ctx, action),
        RuleActionType.SetFrom => SetFrom(ctx, action),
        RuleActionType.SetReplyTo => SetReplyTo(ctx, action),

        // Reject and Discard are decided by the processor, not applied to the message.
        _ => ActionEffect.None(MessageRuleEvaluator.Describe(action)),
    };

    // ---------------------------------------------------------------- recipients

    private static ActionEffect AddRecipient(MessageRuleContext ctx, RuleAction action)
    {
        var address = action.Value?.Trim() ?? string.Empty;
        if (!TryParseMailbox(address, out var mailbox))
            return ActionEffect.None($"add {action.Recipient} {address}", $"'{address}' is not a valid mail address");

        var kind = action.Recipient ?? RecipientKind.To;

        // Bcc deliberately gets no header: Graph derives Bcc as "envelope minus To/Cc headers",
        // so the header would be ignored on delivery and would leak the blind copy into the
        // archived message.
        var headerChanged = false;
        if (kind != RecipientKind.Bcc)
        {
            var list = kind == RecipientKind.Cc ? ctx.Message.Cc : ctx.Message.To;
            if (!list.Mailboxes.Any(m => m.Address.Equals(mailbox.Address, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(mailbox);
                headerChanged = true;
            }
        }

        var envelopeChanged = false;
        if (!ctx.EnvelopeRecipients.Any(r => r.Equals(mailbox.Address, StringComparison.OrdinalIgnoreCase)))
        {
            ctx.EnvelopeRecipients.Add(mailbox.Address);
            envelopeChanged = true;
        }

        return new ActionEffect(headerChanged, envelopeChanged, $"added {mailbox.Address} as {kind}");
    }

    private static ActionEffect RemoveRecipient(MessageRuleContext ctx, RuleAction action)
    {
        var pattern = action.Match ?? string.Empty;
        var removedHeaders = RemoveFromHeaders(ctx, pattern);

        // Removing only from the header would turn the recipient into a Bcc — still delivered,
        // now invisibly. The envelope is what has to lose them.
        var removedEnvelope = ctx.EnvelopeRecipients.RemoveAll(r => AddressMatches(r, pattern));

        var changed = removedHeaders > 0;
        var detail = removedEnvelope > 0 || removedHeaders > 0
            ? $"removed {removedEnvelope} recipient(s) matching '{pattern}'"
            : $"no recipient matched '{pattern}'";

        return new ActionEffect(changed, removedEnvelope > 0, detail);
    }

    private static ActionEffect ReplaceRecipient(MessageRuleContext ctx, RuleAction action)
    {
        var pattern = action.Match ?? string.Empty;
        var replacement = action.Value?.Trim() ?? string.Empty;

        if (!TryParseMailbox(replacement, out var mailbox))
            return ActionEffect.None($"replace '{pattern}'", $"'{replacement}' is not a valid mail address");

        // Keep the replacement in the same list the original sat in, unless the action names one.
        var kind = action.Recipient ?? FindRecipientKind(ctx, pattern) ?? RecipientKind.To;

        var removed = RemoveRecipient(ctx, new RuleAction { Type = RuleActionType.RemoveRecipient, Match = pattern });
        if (!removed.Changed && !removed.EnvelopeChanged)
            return ActionEffect.None($"no recipient matched '{pattern}'");

        var added = AddRecipient(ctx, new RuleAction
        {
            Type = RuleActionType.AddRecipient,
            Value = mailbox.Address,
            Recipient = kind,
        });

        return new ActionEffect(
            removed.Changed || added.Changed,
            removed.EnvelopeChanged || added.EnvelopeChanged,
            $"replaced '{pattern}' with {mailbox.Address} ({kind})",
            added.Warning);
    }

    private static RecipientKind? FindRecipientKind(MessageRuleContext ctx, string pattern)
    {
        if (ctx.Message.To.Mailboxes.Any(m => AddressMatches(m.Address, pattern))) return RecipientKind.To;
        if (ctx.Message.Cc.Mailboxes.Any(m => AddressMatches(m.Address, pattern))) return RecipientKind.Cc;
        return null;
    }

    private static int RemoveFromHeaders(MessageRuleContext ctx, string pattern)
    {
        var removed = 0;
        foreach (var list in new[] { ctx.Message.To, ctx.Message.Cc, ctx.Message.Bcc })
        {
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] is MailboxAddress mailbox && AddressMatches(mailbox.Address, pattern))
                {
                    list.RemoveAt(i);
                    removed++;
                }
            }
        }
        return removed;
    }

    // ---------------------------------------------------------------- subject

    private static ActionEffect SetSubject(MessageRuleContext ctx, string subject)
    {
        var previous = ctx.Message.Subject ?? string.Empty;
        if (previous == subject)
            return ActionEffect.None("subject unchanged");

        ctx.Message.Subject = subject;
        return new ActionEffect(true, false, $"subject set to '{subject}'");
    }

    // ---------------------------------------------------------------- body

    /// <summary>
    /// Inserts a banner into the plain-text and HTML renderings of the message.
    ///
    /// A missing counterpart is never synthesised: adding an HTML part to a text-only message
    /// would flip <c>BuildMessage</c> from a plain-text body to an HTML one and change how every
    /// recipient sees the mail. The action reports what it could not do instead.
    /// </summary>
    private static ActionEffect ModifyBody(MessageRuleContext ctx, RuleAction action, bool prepend)
    {
        var text = action.Value ?? string.Empty;
        var split = ctx.Split;

        if (split.TextBody is null && split.HtmlBody is null)
            return ActionEffect.None("body unchanged", "the message has no text or HTML body part");

        var changed = false;
        var touched = new List<string>();

        if (split.TextBody is { } textPart)
        {
            var existing = textPart.Text ?? string.Empty;
            var combined = prepend ? text + "\r\n\r\n" + existing : existing + "\r\n\r\n" + text;

            // SetText rather than the Text setter: it updates the part's charset parameter too,
            // so a non-ASCII banner cannot mangle an iso-8859-1 body.
            textPart.SetText(Encoding.UTF8, combined);
            changed = true;
            touched.Add("text");
        }

        if (split.HtmlBody is { } htmlPart)
        {
            var fragment = BuildHtmlFragment(action);
            var existing = htmlPart.Text ?? string.Empty;
            htmlPart.SetText(Encoding.UTF8, InsertHtml(existing, fragment, prepend));
            changed = true;
            touched.Add("HTML");
        }

        // Worth saying out loud: on a message that has both, only the HTML banner is delivered —
        // BuildMessage prefers HTML and drops the plain-text alternative.
        var warning = split.TextBody is not null && split.HtmlBody is null
            ? "the message has no HTML part; only the plain-text body was changed"
            : split.HtmlBody is not null && split.TextBody is null
                ? "the message has no plain-text part; only the HTML body was changed"
                : null;

        var where = prepend ? "prepended to" : "appended to";
        return new ActionEffect(changed, false, $"{where} the {string.Join(" and ", touched)} body", warning);
    }

    /// <summary>
    /// The HTML snippet to insert, always wrapped in a single block element so it cannot merge
    /// into the surrounding markup. When the action carries no HTML, it is derived from the text:
    /// escaped first — that is what stops an operator's '&lt;' from becoming markup — then
    /// newlines become line breaks.
    /// </summary>
    internal static string BuildHtmlFragment(RuleAction action)
    {
        if (!string.IsNullOrWhiteSpace(action.Html))
            return $"<div>{action.Html}</div>";

        var escaped = WebUtility.HtmlEncode(action.Value ?? string.Empty)
            .Replace("\r\n", "<br>")
            .Replace("\n", "<br>")
            .Replace("\r", "<br>");

        return $"<div>{escaped}</div>";
    }

    /// <summary>
    /// Inserts the fragment inside the document body. Structural rather than parsed: find the
    /// body tag and splice. A document without one gets the fragment at the very start or end,
    /// which is what a bare HTML fragment needs anyway.
    /// </summary>
    internal static string InsertHtml(string html, string fragment, bool prepend)
    {
        if (prepend)
        {
            var open = RuleRegexCache.Get("<body[^>]*>", caseSensitive: false, timeoutMs: 100);
            if (open is not null)
            {
                try
                {
                    var match = open.Match(html);
                    if (match.Success)
                        return html.Insert(match.Index + match.Length, fragment);
                }
                catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
                {
                    // Fall through to the unanchored insert.
                }
            }
            return fragment + html;
        }

        var close = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return close >= 0 ? html.Insert(close, fragment) : html + fragment;
    }

    // ---------------------------------------------------------------- headers

    private static ActionEffect SetHeader(MessageRuleContext ctx, RuleAction action, bool replace)
    {
        var name = action.HeaderName?.Trim() ?? string.Empty;
        var value = SanitiseHeaderValue(action.Value ?? string.Empty);

        if (name.Length == 0)
            return ActionEffect.None("header unchanged", "no header name given");

        if (replace)
        {
            RemoveAllHeaders(ctx, name);
            ctx.Message.Headers.Add(name, value);
            return new ActionEffect(true, false, $"set header {name}: {value}");
        }

        ctx.Message.Headers.Add(name, value);
        return new ActionEffect(true, false, $"added header {name}: {value}");
    }

    private static ActionEffect RemoveHeader(MessageRuleContext ctx, RuleAction action)
    {
        var name = action.HeaderName?.Trim() ?? string.Empty;
        if (name.Length == 0)
            return ActionEffect.None("header unchanged", "no header name given");

        var removed = RemoveAllHeaders(ctx, name);
        return removed > 0
            ? new ActionEffect(true, false, $"removed {removed} occurrence(s) of header {name}")
            : ActionEffect.None($"header {name} was not present");
    }

    private static int RemoveAllHeaders(MessageRuleContext ctx, string name)
    {
        var removed = 0;
        var headers = ctx.Message.Headers;
        for (var i = headers.Count - 1; i >= 0; i--)
        {
            if (headers[i].Field.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                headers.RemoveAt(i);
                removed++;
            }
        }
        return removed;
    }

    /// <summary>
    /// Strips CR and LF from a header value. A newline would end the header and let the rest of
    /// the value be read as headers of its own — header injection, straight from a config file.
    /// </summary>
    internal static string SanitiseHeaderValue(string value)
        => value.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();

    // ---------------------------------------------------------------- attachments

    private static ActionEffect RemoveAttachments(MessageRuleContext ctx, RuleAction action)
    {
        var selector = action.AttachmentMatch ?? AttachmentMatchMode.NamePattern;
        var value = action.Value?.Trim() ?? string.Empty;

        var doomed = new List<MimeEntity>();
        var skippedInline = 0;

        foreach (var attachment in ctx.Split.Attachments)
        {
            if (!MatchesSelector(attachment, selector, value))
                continue;

            // An inline part is referenced from the HTML body by Content-ID; removing it leaves
            // a dangling cid: reference and a visibly broken message.
            if (attachment.IsInline)
            {
                skippedInline++;
                continue;
            }

            doomed.Add(attachment.Entity);
        }

        if (doomed.Count == 0)
        {
            return ActionEffect.None(
                $"no attachment matched {selector} '{value}'",
                skippedInline > 0 ? $"{skippedInline} matching inline part(s) were kept" : null);
        }

        var removed = 0;
        foreach (var (parent, child) in WalkParts(ctx.Message).ToList())
        {
            if (doomed.Contains(child))
            {
                parent.Remove(child);
                removed++;
            }
        }

        Prune(ctx.Message);
        ctx.InvalidateDerived();

        return new ActionEffect(removed > 0, false,
            $"removed {removed} attachment(s) matching {selector} '{value}'",
            skippedInline > 0 ? $"{skippedInline} matching inline part(s) were kept" : null);
    }

    private static bool MatchesSelector(
        MimeMessageSplitter.SplitAttachment attachment, AttachmentMatchMode mode, string value)
    {
        switch (mode)
        {
            case AttachmentMatchMode.MinSizeBytes:
                return long.TryParse(value, out var min)
                    && MimeMessageSplitter.MeasureEncodedSize(attachment.Entity) >= min;

            case AttachmentMatchMode.Extension:
            {
                var name = MessageRuleEvaluator.FileNameOf(attachment.Entity);
                if (string.IsNullOrEmpty(name)) return false;
                var extension = Path.GetExtension(name);
                if (string.IsNullOrEmpty(extension)) return false;

                return MessageRuleEvaluator.SplitList(value).Any(entry =>
                {
                    var wanted = entry.StartsWith('.') ? entry : "." + entry;
                    return extension.Equals(wanted, StringComparison.OrdinalIgnoreCase);
                });
            }

            default:
            {
                var name = MessageRuleEvaluator.FileNameOf(attachment.Entity);
                return !string.IsNullOrEmpty(name)
                    && RuleRegexCache.IsMatch(
                        name, RuleRegexCache.WildcardToRegex(value), caseSensitive: false, timeoutMs: 100, out _);
            }
        }
    }

    /// <summary>Every (container, child) pair in the tree — removal needs the parent.</summary>
    private static IEnumerable<(Multipart Parent, MimeEntity Child)> WalkParts(MimeMessage message)
    {
        if (message.Body is not Multipart root)
            yield break;

        var stack = new Stack<Multipart>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var child in current)
            {
                yield return (current, child);
                if (child is Multipart nested)
                    stack.Push(nested);
            }
        }
    }

    /// <summary>
    /// Tidies the tree after a removal: an emptied container disappears, and a container left
    /// with a single child is replaced by that child so the message does not keep a
    /// multipart/mixed wrapper around one body part.
    /// </summary>
    private static void Prune(MimeMessage message)
    {
        if (message.Body is not null)
            message.Body = PruneEntity(message.Body)!;

        // A message must have a body; an attachment-only mail whose attachments all went away
        // would otherwise serialise into something no parser accepts.
        message.Body ??= new TextPart("plain") { Text = string.Empty };
    }

    private static MimeEntity? PruneEntity(MimeEntity entity)
    {
        if (entity is not Multipart multipart)
            return entity;

        for (var i = multipart.Count - 1; i >= 0; i--)
        {
            var pruned = PruneEntity(multipart[i]);
            if (pruned is null)
                multipart.RemoveAt(i);
            else if (!ReferenceEquals(pruned, multipart[i]))
                multipart[i] = pruned;
        }

        if (multipart.Count == 0)
            return null;

        // Signed and encrypted containers keep their shape whatever happens — their structure
        // is the protection. (Body and attachment actions never reach them anyway.)
        var isProtected = multipart.ContentType.IsMimeType("multipart", "signed")
                          || multipart.ContentType.IsMimeType("multipart", "encrypted");

        return multipart.Count == 1 && !isProtected ? multipart[0] : multipart;
    }

    // ---------------------------------------------------------------- misc

    private static ActionEffect SetImportance(MessageRuleContext ctx, RuleAction action)
    {
        var token = action.Value?.Trim() ?? string.Empty;
        var importance = token switch
        {
            var t when t.Equals("High", StringComparison.OrdinalIgnoreCase) => MessageImportance.High,
            var t when t.Equals("Low", StringComparison.OrdinalIgnoreCase) => MessageImportance.Low,
            var t when t.Equals("Normal", StringComparison.OrdinalIgnoreCase) => MessageImportance.Normal,
            _ => (MessageImportance?)null,
        };

        if (importance is null)
            return ActionEffect.None("importance unchanged", $"'{token}' is not Low, Normal or High");

        ctx.Message.Importance = importance.Value;
        return new ActionEffect(true, false, $"importance set to {importance.Value}");
    }

    /// <summary>
    /// Rewrites the From header <b>and</b> the envelope sender. The sending mailbox is picked
    /// from the envelope sender in <c>QueueProcessor</c>, so changing only the header would send
    /// the mail as the original mailbox with a mismatched From — which Exchange refuses as
    /// ErrorSendAsDenied.
    /// </summary>
    private static ActionEffect SetFrom(MessageRuleContext ctx, RuleAction action)
    {
        var address = action.Value?.Trim() ?? string.Empty;
        if (!TryParseMailbox(address, out var mailbox))
            return ActionEffect.None("From unchanged", $"'{address}' is not a valid mail address");

        ctx.Message.From.Clear();
        ctx.Message.From.Add(mailbox);
        ctx.EnvelopeFrom = mailbox.Address;

        return new ActionEffect(true, true, $"From set to {mailbox.Address}");
    }

    private static ActionEffect SetReplyTo(MessageRuleContext ctx, RuleAction action)
    {
        var address = action.Value?.Trim() ?? string.Empty;
        if (!TryParseMailbox(address, out var mailbox))
            return ActionEffect.None("Reply-To unchanged", $"'{address}' is not a valid mail address");

        ctx.Message.ReplyTo.Clear();
        ctx.Message.ReplyTo.Add(mailbox);
        return new ActionEffect(true, false, $"Reply-To set to {mailbox.Address}");
    }

    // ---------------------------------------------------------------- helpers

    internal static bool TryParseMailbox(string address, out MailboxAddress mailbox)
    {
        try
        {
            mailbox = MailboxAddress.Parse(address);
            return !string.IsNullOrWhiteSpace(mailbox.Address);
        }
        catch (ParseException)
        {
            mailbox = null!;
            return false;
        }
    }

    /// <summary>
    /// Matches an address against a ';'-separated pattern list. Entries may be an exact address,
    /// an '@domain' wildcard (exact domain, matching the sender/recipient lists) or a '*'/'?'
    /// wildcard.
    /// </summary>
    internal static bool AddressMatches(string address, string pattern)
    {
        foreach (var entry in MessageRuleEvaluator.SplitList(pattern))
        {
            if (entry.StartsWith('@'))
            {
                if (address.EndsWith(entry, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (entry.Contains('*') || entry.Contains('?'))
            {
                if (RuleRegexCache.IsMatch(
                        address, RuleRegexCache.WildcardToRegex(entry), caseSensitive: false, timeoutMs: 100, out _))
                    return true;
            }
            else if (address.Equals(entry, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
