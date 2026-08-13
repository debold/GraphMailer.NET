using System.Security.Cryptography;
using System.Text;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GraphMailer.Service.Infrastructure.Security.Amsi;

/// <summary>
/// Scans messages through AMSI, part by part.
///
/// The decisive detail is <i>what</i> is handed to the provider: pushing the raw EML through
/// would scan base64 text, where no signature matches. Every part is therefore decoded first,
/// using <see cref="MimeMessageSplitter"/> — the same classification the Graph delivery uses,
/// so what gets scanned is what actually gets sent.
///
/// One AMSI session spans a whole message so a provider can correlate its parts, which is
/// exactly what sessions exist for. Session handles are not thread-safe, hence one session at a
/// time and strictly sequential scans within it.
/// </summary>
internal sealed class AmsiContentScanner : IMailContentScanner, IDisposable
{
    /// <summary>Passed to AmsiInitialize; appears in provider-side telemetry as the calling app.</summary>
    private const string AppName = "GraphMailer";

    /// <summary>
    /// How deep <c>message/rfc822</c> nesting is followed. Unlike delivery — which keeps an
    /// attached mail as one opaque unit — scanning must look inside, or a forwarded mail's
    /// attachments would only ever be seen base64-encoded. Bounded so a crafted nesting chain
    /// cannot turn one message into unbounded work.
    /// </summary>
    private const int MaxNestingDepth = 5;

    /// <summary>
    /// Concurrent scans. Each holds a decoded copy of a part while the native call runs, so this
    /// caps peak memory as much as it caps CPU. Fixed rather than configurable: it is a safety
    /// limit, not a tuning knob, and the SMTP timeout already bounds the resulting wait.
    /// </summary>
    private const int MaxConcurrentScans = 4;

    private readonly IOptionsMonitor<MalwareScanOptions> _options;
    private readonly ILogger<AmsiContentScanner> _logger;
    private readonly AmsiContextHandle? _context;
    private readonly SemaphoreSlim _gate = new(MaxConcurrentScans, MaxConcurrentScans);

    public bool IsAvailable { get; }
    public IReadOnlyList<AmsiProvider> Providers { get; }

    public AmsiContentScanner(
        IOptionsMonitor<MalwareScanOptions> options,
        ILogger<AmsiContentScanner> logger)
    {
        _options = options;
        _logger = logger;
        Providers = AmsiProviderRegistry.Enumerate();

        if (Providers.Count == 0)
        {
            // Security-relevant degradation with no other operator channel: scanning is
            // configured but nothing will ever be inspected. AmsiInitialize would still
            // succeed here and every scan would come back "not detected", which reads
            // exactly like clean mail — so this must be loud.
            _logger.LogError(
                "[MalwareScan] No AMSI provider is registered on this machine – malware scanning is inactive. " +
                "Install an AMSI-capable antivirus product (Microsoft Defender registers one automatically).");
            return;
        }

        var hr = AmsiNativeMethods.AmsiInitialize(AppName, out var context);
        if (hr != AmsiNativeMethods.Ok || context.IsInvalid)
        {
            context.Dispose();
            _logger.LogError(
                "[MalwareScan] AmsiInitialize failed (HRESULT 0x{Hr:X8}) – malware scanning is inactive.", hr);
            return;
        }

        _context = context;
        IsAvailable = true;
        _logger.LogInformation(
            "[MalwareScan] Scanner ready in {Mode} mode using {ProviderCount} AMSI provider(s): {Providers}",
            _options.CurrentValue.Mode, Providers.Count, string.Join(", ", Providers.Select(p => p.Describe())));
    }

    public async Task<ScanResult> ScanAsync(ReadOnlyMemory<byte> eml, string messageId, CancellationToken ct = default)
    {
        if (!IsAvailable || _context is null) return ScanResult.Unavailable();

        var opts = _options.CurrentValue;
        var timeout = TimeSpan.FromSeconds(Math.Max(1, opts.TimeoutSeconds));

        try
        {
            return await Task.Run(() => ScanCore(eml, messageId, opts), ct).WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            // AmsiScanBuffer is a blocking native call and cannot be cancelled: the orphaned
            // task runs to completion in the background and releases the gate itself. We only
            // stop waiting for its answer — and deliver the message unscanned.
            _logger.LogWarning(
                "[MalwareScan] Scan of {MessageId} timed out after {Timeout}s – message is delivered unscanned",
                messageId, timeout.TotalSeconds);
            return ScanResult.Failed($"scan timed out after {timeout.TotalSeconds:F0}s");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[MalwareScan] Scan of {MessageId} failed: {Error} – message is delivered unscanned",
                messageId, ex.Message);
            return ScanResult.Failed(ex.Message);
        }
    }

    private ScanResult ScanCore(ReadOnlyMemory<byte> eml, string messageId, MalwareScanOptions opts)
    {
        _gate.Wait();
        try
        {
            var message = TryParse(eml);
            var hr = AmsiNativeMethods.AmsiOpenSession(_context!, out var session);
            if (hr != AmsiNativeMethods.Ok)
                return ScanResult.Failed($"AmsiOpenSession failed (HRESULT 0x{hr:X8})");

            try
            {
                // Unparsable MIME still gets looked at, just as one opaque blob. Worse coverage
                // than per-part scanning, but the alternative is delivering it wholly unchecked.
                return message is null
                    ? ScanRaw(eml, messageId, session, opts)
                    : ScanParts(message, messageId, session, opts);
            }
            finally
            {
                AmsiNativeMethods.AmsiCloseSession(_context!, session);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private ScanResult ScanParts(MimeMessage message, string messageId, IntPtr session, MalwareScanOptions opts)
    {
        var skipped = default(ScanResult?);

        foreach (var target in EnumerateTargets(message, depth: 0))
        {
            if (target.EncodedSize > opts.MaxScanBytes)
            {
                _logger.LogWarning(
                    "[MalwareScan] {MessageId}: part '{Part}' ({Size} bytes) exceeds MaxScanBytes ({Limit}) " +
                    "and was not scanned – message is delivered",
                    messageId, target.Name, target.EncodedSize, opts.MaxScanBytes);
                skipped ??= ScanResult.Skipped(target.Name, target.EncodedSize);
                continue;
            }

            var data = target.Load();
            if (data.Length == 0) continue;

            var hr = AmsiNativeMethods.AmsiScanBuffer(
                _context!, data, (uint)data.Length, target.Name, session, out var result);

            if (hr != AmsiNativeMethods.Ok)
                return ScanResult.Failed($"AmsiScanBuffer failed for '{target.Name}' (HRESULT 0x{hr:X8})");

            _logger.LogDebug(
                "[MalwareScan] {MessageId}: scanned '{Part}' ({Size} bytes) → AMSI result {Result}",
                messageId, target.Name, data.Length, result);

            if (!AmsiResult.IsMalware(result)) continue;

            // Hash only on a hit: the allowlist is the sole consumer, and hashing every part of
            // every message would cost real time for content nobody will ever exempt.
            return new ScanResult(
                ScanOutcome.Malware,
                ThreatLocation: target.Name,
                Sha256: target.IsAttachment ? Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() : null,
                PartSizeBytes: data.Length,
                ResultCode: result);
        }

        return skipped ?? ScanResult.Clean();
    }

    private ScanResult ScanRaw(ReadOnlyMemory<byte> eml, string messageId, IntPtr session, MalwareScanOptions opts)
    {
        if (eml.Length > opts.MaxScanBytes)
            return ScanResult.Skipped("raw message", eml.Length);

        var data = eml.ToArray();
        var hr = AmsiNativeMethods.AmsiScanBuffer(
            _context!, data, (uint)data.Length, $"{messageId}.eml", session, out var result);

        if (hr != AmsiNativeMethods.Ok)
            return ScanResult.Failed($"AmsiScanBuffer failed for the raw message (HRESULT 0x{hr:X8})");

        // No hash: an allowlist entry for a whole message body would never match a second mail.
        return AmsiResult.IsMalware(result)
            ? new ScanResult(ScanOutcome.Malware, "raw message (unparsable MIME)", null, data.Length, result)
            : ScanResult.Clean();
    }

    /// <summary>
    /// Walks a message into the flat list of parts that get scanned, in scan order. Internal so
    /// the classification — nesting, depth limit, body vs. attachment — is testable without a
    /// live AMSI provider, which is the half of this class a unit test can actually pin down.
    /// </summary>
    internal static IEnumerable<ScanTarget> EnumerateTargets(MimeMessage message, int depth)
    {
        var split = MimeMessageSplitter.Split(message);

        if (split.TextBody is not null)
            yield return BodyTarget(split.TextBody, "message body (text)");

        if (split.HtmlBody is not null)
            yield return BodyTarget(split.HtmlBody, "message body (html)");

        foreach (var attachment in split.Attachments)
        {
            if (attachment.Entity is MessagePart nested)
            {
                // Below the depth limit the inner mail is walked like the outer one; at the limit
                // it is scanned whole, so a deeply nested chain is degraded, never ignored.
                if (depth < MaxNestingDepth && nested.Message is not null)
                {
                    foreach (var inner in EnumerateTargets(nested.Message, depth + 1))
                        yield return inner;
                }
                else
                {
                    yield return EntityTarget(attachment.Entity, "attached message");
                }
                continue;
            }

            if (attachment.Entity is MimePart part)
            {
                var name = FirstNonEmpty(part.FileName, part.ContentType?.Name) ?? "attachment";
                yield return new ScanTarget(
                    name,
                    IsAttachment: true,
                    MimeMessageSplitter.MeasureEncodedSize(part),
                    () => DecodeContent(part));
            }
        }
    }

    private static ScanTarget BodyTarget(TextPart part, string label)
        => new(label, IsAttachment: false, MimeMessageSplitter.MeasureEncodedSize(part),
               () => Encoding.UTF8.GetBytes(part.Text ?? string.Empty));

    private static ScanTarget EntityTarget(MimeEntity entity, string label)
        => new(label, IsAttachment: false, MimeMessageSplitter.MeasureEncodedSize(entity), () =>
        {
            using var buffer = new MemoryStream();
            entity.WriteTo(buffer);
            return buffer.ToArray();
        });

    private static byte[] DecodeContent(MimePart part)
    {
        if (part.Content is null) return [];
        using var buffer = new MemoryStream();
        part.Content.DecodeTo(buffer);
        return buffer.ToArray();
    }

    private static string? FirstNonEmpty(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

    private static MimeMessage? TryParse(ReadOnlyMemory<byte> eml)
    {
        try
        {
            using var stream = System.Runtime.InteropServices.MemoryMarshal.TryGetArray(eml, out var segment)
                ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
                : new MemoryStream(eml.ToArray(), writable: false);
            return MimeMessage.Load(stream);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _context?.Dispose();
        _gate.Dispose();
    }
}
