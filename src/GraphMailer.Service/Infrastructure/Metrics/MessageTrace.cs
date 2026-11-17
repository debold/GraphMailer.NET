namespace GraphMailer.Service.Infrastructure.Metrics;

/// <summary>
/// One step in a message's history, shown as one line of the ConfigTool's message timeline
/// ("Malware scan: clean", "Attempt 2 failed: 503 ServiceUnavailable", "Delivered").
/// </summary>
/// <param name="OccurredAtUtc">When the step happened (UTC).</param>
/// <param name="Stage">Which part of the pipeline wrote it — one of <see cref="TraceStages"/>.</param>
/// <param name="Outcome">How it went — one of <see cref="TraceOutcomes"/>.</param>
/// <param name="Summary">One line for the timeline, written for an operator.</param>
/// <param name="Detail">Optional second line: codes, durations, the next attempt.</param>
/// <param name="Attempt">Graph delivery attempt number (1-based), null for other stages.</param>
internal sealed record MessageTraceEvent(
    DateTime OccurredAtUtc,
    string Stage,
    string Outcome,
    string Summary,
    string? Detail = null,
    int? Attempt = null);

/// <summary>
/// What the message list shows about a message. Written with every trace update; fields that are
/// empty in a later update never overwrite what an earlier one stored.
/// </summary>
internal sealed record MessageTraceHeader
{
    public required string MessageId { get; init; }
    public required DateTime ReceivedAtUtc { get; init; }
    public string From { get; init; } = string.Empty;
    public IReadOnlyList<string> To { get; init; } = [];
    public string Subject { get; init; } = string.Empty;
    public string ClientIp { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
}

/// <summary>Values of <c>message_trace_events.stage</c> (stored verbatim).</summary>
internal static class TraceStages
{
    public const string Smtp = "smtp";
    public const string Scan = "scan";
    public const string Rules = "rules";
    public const string Queue = "queue";
    public const string Graph = "graph";
}

/// <summary>Values of <c>message_trace_events.outcome</c> (stored verbatim).</summary>
internal static class TraceOutcomes
{
    /// <summary>The step succeeded.</summary>
    public const string Ok = "ok";
    /// <summary>Neutral information, e.g. a skipped scan.</summary>
    public const string Info = "info";
    /// <summary>Something went wrong but the message continues (audit-mode finding, unscanned).</summary>
    public const string Warning = "warning";
    /// <summary>A delivery attempt failed and another one is scheduled.</summary>
    public const string Retry = "retry";
    /// <summary>The message stopped here: rejected, discarded or given up.</summary>
    public const string Failed = "failed";
}

/// <summary>Values of <c>message_traces.status</c> (stored verbatim).</summary>
internal static class TraceStatuses
{
    /// <summary>Accepted and waiting for delivery (also after failed attempts).</summary>
    public const string Queued = "queued";
    public const string Sent = "sent";
    /// <summary>Accepted, but delivery was given up.</summary>
    public const string Failed = "failed";
    /// <summary>Refused during the SMTP session; the client got an error reply.</summary>
    public const string Rejected = "rejected";
    /// <summary>The client got 250, but a rule dropped the message.</summary>
    public const string Discarded = "discarded";
}
