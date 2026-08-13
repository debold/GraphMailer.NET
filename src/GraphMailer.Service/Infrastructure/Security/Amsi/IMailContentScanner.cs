namespace GraphMailer.Service.Infrastructure.Security.Amsi;

/// <summary>
/// Outcome of scanning one message. Only <see cref="Malware"/> may block delivery —
/// every other value is a reason the message was <i>not</i> fully vetted, and the caller
/// delivers it anyway (fail-open). Keeping the non-blocking cases as distinct values rather
/// than collapsing them into "clean" is what lets the operator see coverage gaps.
/// </summary>
internal enum ScanOutcome
{
    /// <summary>Every part was scanned and none was flagged.</summary>
    Clean,

    /// <summary>A part was flagged. The only outcome that blocks.</summary>
    Malware,

    /// <summary>At least one part exceeded the size limit and was not scanned.</summary>
    Skipped,

    /// <summary>No AMSI provider, or the API could not be initialised.</summary>
    Unavailable,

    /// <summary>The scan errored or timed out.</summary>
    Failed,
}

/// <param name="ThreatLocation">
/// Attachment file name, or a "message body" label. Never sent to the SMTP client —
/// the reply stays generic so nothing about the detection leaks to the sender.
/// </param>
/// <param name="Sha256">
/// Hash of the offending attachment's decoded bytes, for the false-positive allowlist.
/// <see langword="null"/> for body hits: a message body differs on every mail, so hashing
/// it would produce an allowlist entry that never matches again.
/// </param>
/// <param name="ResultCode">Raw <c>AMSI_RESULT</c>. AMSI exposes no threat name, so this is all the detail there is.</param>
internal readonly record struct ScanResult(
    ScanOutcome Outcome,
    string? ThreatLocation = null,
    string? Sha256 = null,
    long PartSizeBytes = 0,
    uint ResultCode = 0,
    string? Error = null)
{
    internal static ScanResult Clean() => new(ScanOutcome.Clean);
    internal static ScanResult Unavailable() => new(ScanOutcome.Unavailable);
    internal static ScanResult Failed(string error) => new(ScanOutcome.Failed, Error: error);
    internal static ScanResult Skipped(string location, long size)
        => new(ScanOutcome.Skipped, ThreatLocation: location, PartSizeBytes: size);

    /// <summary>True when a hash-based allowlist entry could apply — attachments only.</summary>
    internal bool IsAllowlistable => Outcome == ScanOutcome.Malware && !string.IsNullOrEmpty(Sha256);
}

/// <summary>One part of a message as handed to the scanner.</summary>
/// <param name="Name">Attachment file name or body label; passed to AMSI as the content name hint.</param>
/// <param name="IsAttachment">Only attachments can be exempted by hash — a body differs per message.</param>
/// <param name="EncodedSize">
/// Still-encoded size, used for the size check before anything is decoded. Conservative by
/// design: base64 inflates by roughly a third, so a part may be skipped slightly below the
/// limit rather than decoded just to measure it.
/// </param>
/// <param name="Load">Decodes the part. Lazy, so an oversized part is never materialised.</param>
internal readonly record struct ScanTarget(string Name, bool IsAttachment, long EncodedSize, Func<byte[]> Load);

/// <summary>
/// Scans a received message for malware before it is queued. Implemented over AMSI, so the
/// actual engine is whichever antimalware product registered a provider on this machine.
/// </summary>
internal interface IMailContentScanner
{
    /// <summary>
    /// False when no provider is registered or initialisation failed. The caller skips scanning
    /// entirely — a configured-but-unavailable scanner never blocks mail.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Providers found at startup; surfaced by the ConfigTool so the state is visible.</summary>
    IReadOnlyList<AmsiProvider> Providers { get; }

    /// <param name="eml">Raw RFC-5321 message as received.</param>
    /// <param name="messageId">Correlation id for log lines only.</param>
    Task<ScanResult> ScanAsync(ReadOnlyMemory<byte> eml, string messageId, CancellationToken ct = default);
}
