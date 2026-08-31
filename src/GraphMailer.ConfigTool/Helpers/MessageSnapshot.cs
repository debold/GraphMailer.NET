using GraphMailer.Service.Infrastructure.Rules;
using GraphMailer.Service.Services;
using MimeKit;

namespace GraphMailer.ConfigTool.Helpers;

/// <summary>One field that a rule set changed.</summary>
internal readonly record struct MessageChange(string Field, string Before, string After);

/// <summary>
/// What a message looks like at one point in time, and what changed between two of them.
///
/// The rule tester used to print the whole message twice and leave the reader to spot the
/// difference. Everything unchanged is noise there — the one thing worth seeing is what the rules
/// actually did, so the comparison happens here and only differences are reported.
/// </summary>
internal sealed record MessageSnapshot
{
    internal required string From { get; init; }
    internal required IReadOnlyList<string> To { get; init; }
    internal required IReadOnlyList<string> Cc { get; init; }
    internal required IReadOnlyList<string> Bcc { get; init; }
    internal required IReadOnlyList<string> Envelope { get; init; }
    internal required string Subject { get; init; }
    internal required string Importance { get; init; }
    internal required IReadOnlyList<string> Attachments { get; init; }
    internal required IReadOnlyList<string> Headers { get; init; }
    internal required string BodyText { get; init; }
    internal required string BodyHtml { get; init; }

    /// <summary>
    /// Header addresses the envelope does not confirm. They look like recipients in the message
    /// and are dropped on delivery, so they are worth naming even when nothing changed them.
    /// </summary>
    internal required IReadOnlyList<string> NotDelivered { get; init; }

    internal static MessageSnapshot Capture(
        MimeMessage message, string envelopeFrom, IReadOnlyList<string> recipients)
    {
        var split = MimeMessageSplitter.Split(message);

        var to = message.To.Mailboxes.Select(m => m.Address).ToList();
        var cc = message.Cc.Mailboxes.Select(m => m.Address).ToList();

        // Bcc is derived, never read from a header — exactly the way GraphApiClient.BuildMessage
        // derives it: every envelope recipient that appears in neither the To nor the Cc header.
        var headerAddresses = new HashSet<string>(to.Concat(cc), StringComparer.OrdinalIgnoreCase);
        var envelope = new HashSet<string>(recipients, StringComparer.OrdinalIgnoreCase);

        return new MessageSnapshot
        {
            From = envelopeFrom,
            To = to,
            Cc = cc,
            Bcc = [.. recipients.Where(r => !headerAddresses.Contains(r))],
            Envelope = [.. recipients],
            Subject = message.Subject ?? string.Empty,
            Importance = MessageRuleEvaluator.ImportanceToken(message),
            Attachments = [.. split.Attachments.Select(a =>
                $"{MessageRuleEvaluator.FileNameOf(a.Entity)} ({MimeMessageSplitter.MeasureEncodedSize(a.Entity)} B)")],
            Headers = [.. message.Headers.Select(h => $"{h.Field}: {h.Value}")],
            BodyText = split.TextBody?.Text ?? string.Empty,
            BodyHtml = split.HtmlBody?.Text ?? string.Empty,
            NotDelivered = [.. headerAddresses.Where(h => !envelope.Contains(h))],
        };
    }

    /// <summary>
    /// The fields that differ, in the order an operator reads a message. An empty result means
    /// the rules left the message exactly as it arrived.
    /// </summary>
    internal static IReadOnlyList<MessageChange> Diff(MessageSnapshot before, MessageSnapshot after)
    {
        var changes = new List<MessageChange>();

        Compare("From", before.From, after.From);
        CompareList("To", before.To, after.To);
        CompareList("Cc", before.Cc, after.Cc);
        CompareList("Bcc", before.Bcc, after.Bcc);
        CompareList("Envelope", before.Envelope, after.Envelope);
        Compare("Subject", before.Subject, after.Subject);
        Compare("Importance", before.Importance, after.Importance);
        CompareList("Attachments", before.Attachments, after.Attachments);
        CompareHeaders(before.Headers, after.Headers);
        Compare("Text body", Shorten(before.BodyText), Shorten(after.BodyText));
        Compare("HTML body", Shorten(before.BodyHtml), Shorten(after.BodyHtml));

        return changes;

        void Compare(string field, string a, string b)
        {
            if (!string.Equals(a, b, StringComparison.Ordinal))
                changes.Add(new MessageChange(field, Display(a), Display(b)));
        }

        void CompareList(string field, IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            // Order is not meaningful for an address list, so a reordering is not a change.
            if (!a.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                  .SequenceEqual(b.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
                changes.Add(new MessageChange(field, Display(Join(a)), Display(Join(b))));
        }

        // Headers are compared entry by entry: a message carries dozens, and printing the whole
        // set because one was added would bury the one thing that happened.
        //
        // Headers that already have a row of their own are left out. Setting a subject changes
        // both Subject and the "Subject:" header, and reporting that twice makes a one-line change
        // look like two — the very noise this comparison exists to remove.
        void CompareHeaders(IReadOnlyList<string> all, IReadOnlyList<string> allAfter)
        {
            var a = all.Where(h => !HasOwnRow(NameOf(h))).ToList();
            var b = allAfter.Where(h => !HasOwnRow(NameOf(h))).ToList();

            var removed = a.Except(b, StringComparer.OrdinalIgnoreCase).ToList();
            var added = b.Except(a, StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var header in removed.Where(h => !added.Any(x => SameName(x, h))))
                changes.Add(new MessageChange("Header", header, "(removed)"));

            foreach (var header in added)
            {
                var replaced = removed.FirstOrDefault(x => SameName(x, header));
                changes.Add(new MessageChange("Header", replaced ?? "(none)", header));
            }
        }
    }

    /// <summary>
    /// Headers the comparison reports through a dedicated row instead. Reply-To is deliberately
    /// absent: it has no row, so a rule that sets it should surface as a header change.
    /// </summary>
    private static readonly string[] HeadersWithOwnRow =
        ["Subject", "From", "To", "Cc", "Bcc", "Importance", "X-Priority", "Priority"];

    private static bool HasOwnRow(string headerName)
        => HeadersWithOwnRow.Contains(headerName, StringComparer.OrdinalIgnoreCase);

    private static bool SameName(string a, string b)
        => string.Equals(NameOf(a), NameOf(b), StringComparison.OrdinalIgnoreCase);

    private static string NameOf(string header)
    {
        var separator = header.IndexOf(':');
        return separator <= 0 ? header : header[..separator];
    }

    private static string Join(IReadOnlyList<string> values)
        => values.Count == 0 ? string.Empty : string.Join(", ", values);

    private static string Display(string value)
        => string.IsNullOrEmpty(value) ? "(none)" : value;

    /// <summary>Bodies can be long; a change is visible from the first line.</summary>
    private static string Shorten(string value)
    {
        var single = value.ReplaceLineEndings(" ").Trim();
        return single.Length <= 120 ? single : single[..117] + "…";
    }
}
