using GraphMailer.Service.Services;
using MimeKit;

namespace GraphMailer.Service.Infrastructure.Rules;

/// <summary>
/// The session facts a rule can look at. Passed in explicitly rather than read from
/// <c>ISessionContext</c> so the ConfigTool's rule tester can build one from a form and drive
/// the exact same engine.
/// </summary>
internal sealed record RuleSessionFacts
{
    internal string ClientIp { get; init; } = string.Empty;
    internal int ListenerPort { get; init; }
    internal string AuthUser { get; init; } = string.Empty;
    internal bool Authenticated { get; init; }
    internal bool Tls { get; init; }
}

/// <summary>
/// Everything a rule evaluates against, plus the mutable state the actions change.
///
/// The envelope is deliberately part of the mutable state: delivery follows the SMTP envelope,
/// not the message headers (see <c>GraphApiClient.BuildMessage</c>), so a recipient action that
/// only touched the headers would change what the mail <i>looks</i> like without changing where
/// it goes.
///
/// Body, attachment and protection views are materialised on first use and thrown away again by
/// <see cref="InvalidateDerived"/> after a structural change, so a rule set that never asks about
/// the body never decodes one.
/// </summary>
internal sealed class MessageRuleContext
{
    private MimeMessageSplitter.SplitResult? _split;
    private string? _bodyText;
    private string? _bodyHtml;
    private MimeProtectionKind? _protection;

    private MessageRuleContext(MimeMessage message, long sizeBytes, long maxBodyScanBytes)
    {
        Message = message;
        MessageSizeBytes = sizeBytes;
        MaxBodyScanBytes = maxBodyScanBytes;
    }

    internal MimeMessage Message { get; }
    internal long MessageSizeBytes { get; }
    internal long MaxBodyScanBytes { get; }

    internal string MessageId { get; private init; } = string.Empty;
    internal RuleSessionFacts Session { get; private init; } = new();

    /// <summary>Mutable: <c>SetFrom</c> rewrites it, and it decides the sending mailbox.</summary>
    internal string EnvelopeFrom { get; set; } = string.Empty;

    /// <summary>Mutable: this list, not the headers, decides who receives the message.</summary>
    internal List<string> EnvelopeRecipients { get; } = [];

    /// <summary>
    /// True when the message could not be parsed. The processor then skips every rule and lets
    /// the message through untouched — the same fail-open stance the malware scan takes.
    /// </summary>
    internal bool ParseFailed { get; private init; }

    /// <summary>Set when a body condition had to work on a truncated body.</summary>
    internal bool BodyTruncated { get; private set; }

    internal MimeMessageSplitter.SplitResult Split => _split ??= MimeMessageSplitter.Split(Message);

    internal MimeProtectionKind Protection => _protection ??= MimeProtection.Classify(Message);

    internal string BodyText => _bodyText ??= Cap(Split.TextBody?.Text ?? string.Empty);

    internal string BodyHtml => _bodyHtml ??= Cap(Split.HtmlBody?.Text ?? string.Empty);

    /// <summary>Drops the cached views after an action changed the message structure.</summary>
    internal void InvalidateDerived()
    {
        _split = null;
        _bodyText = null;
        _bodyHtml = null;
        _protection = null;
    }

    /// <summary>
    /// Builds a context from received bytes. A parse failure yields a context flagged
    /// <see cref="ParseFailed"/> rather than an exception — the caller decides what that means,
    /// and for the relay it means "deliver unmodified".
    /// </summary>
    internal static MessageRuleContext Create(
        byte[] emlBytes,
        string envelopeFrom,
        IReadOnlyList<string> recipients,
        RuleSessionFacts session,
        string messageId = "",
        long maxBodyScanBytes = 1_048_576)
    {
        MimeMessage message;
        var parseFailed = false;
        try
        {
            using var stream = new MemoryStream(emlBytes, writable: false);
            message = MimeMessage.Load(stream);
        }
        catch
        {
            message = new MimeMessage();
            parseFailed = true;
        }

        var ctx = new MessageRuleContext(message, emlBytes.LongLength, maxBodyScanBytes)
        {
            MessageId = messageId,
            Session = session,
            ParseFailed = parseFailed,
            EnvelopeFrom = envelopeFrom,
        };
        ctx.EnvelopeRecipients.AddRange(recipients);
        return ctx;
    }

    /// <summary>Context around an already-parsed message — used by the ConfigTool rule tester.</summary>
    internal static MessageRuleContext FromMessage(
        MimeMessage message,
        string envelopeFrom,
        IReadOnlyList<string> recipients,
        RuleSessionFacts session,
        long sizeBytes = 0,
        long maxBodyScanBytes = 1_048_576)
    {
        var ctx = new MessageRuleContext(message, sizeBytes, maxBodyScanBytes)
        {
            Session = session,
            EnvelopeFrom = envelopeFrom,
        };
        ctx.EnvelopeRecipients.AddRange(recipients);
        return ctx;
    }

    /// <summary>
    /// Truncates rather than skips. Skipping an oversized body would make the condition false,
    /// and a <i>negated</i> condition would then fire on every large message — a silent
    /// inversion of what the operator wrote. A prefix at least keeps the comparison monotone.
    /// </summary>
    private string Cap(string value)
    {
        if (MaxBodyScanBytes <= 0 || value.Length <= MaxBodyScanBytes)
            return value;

        BodyTruncated = true;
        return value[..(int)Math.Min(MaxBodyScanBytes, int.MaxValue)];
    }
}
