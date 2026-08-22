using GraphMailer.Service.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GraphMailer.Service.Infrastructure.Security.Amsi;

/// <summary>What a self-test run established about the local scanning stack.</summary>
internal enum AmsiSelfTestOutcome
{
    /// <summary>The engine flagged the probe — the whole path works end to end.</summary>
    Detected,

    /// <summary>The scan completed but the probe came back clean; the provider ignores the sample.</summary>
    NotDetected,

    /// <summary>No provider registered, or AMSI could not be initialised.</summary>
    Unavailable,

    /// <summary>The scan errored, timed out, or the probe was skipped by a limit.</summary>
    Failed,
}

/// <param name="Detail">Operator-facing explanation; already phrased for direct display.</param>
internal readonly record struct AmsiSelfTestResult(AmsiSelfTestOutcome Outcome, string Detail);

/// <summary>
/// Verifies that malware scanning actually works on this machine, by running the standard
/// antivirus test file through the real <see cref="AmsiContentScanner"/> rather than merely asking
/// the registry whether a provider exists.
///
/// Going through the full scanner is the point: enumerating providers proves only that an
/// antivirus product is installed, while a probe exercises AmsiInitialize, the session, the MIME
/// split, the per-part decode and the result mapping — the parts that can be misconfigured. The
/// probe is built as an <i>attachment</i> because that is the path that matters in practice and
/// the one where decoding-before-scanning is load-bearing.
///
/// Scope: this runs in the calling process (the ConfigTool), not in the service. Same machine and
/// therefore the same providers, so the answer is representative — but it does not prove the
/// running service's scanner is up. The service reports that itself with its startup line.
/// </summary>
internal static class AmsiSelfTest
{
    /// <summary>Name handed to AMSI as the content hint, and shown to the operator.</summary>
    internal const string AttachmentName = "amsi-selftest.txt";

    /// <summary>
    /// The industry-standard antivirus test file — harmless content every scanning engine is
    /// expected to report as malicious, and the sanctioned way to check that detection responds.
    /// Chosen over the AMSI-specific test sample because this probe travels as an <i>attachment</i>:
    /// the file signature is what an attachment scan matches on, and it is recognised across
    /// products rather than by one vendor.
    ///
    /// Stored XOR-masked rather than as a literal, for the same reason the round-trip test does it:
    /// a plain literal would put the signature into this source file and the compiled assembly,
    /// where the antivirus on the machine would flag and quarantine them — breaking the working
    /// copy (and the OneDrive sync it lives in) and the build output. Splitting a literal is not
    /// enough, because the compiler folds adjacent string literals back into one. The masked bytes
    /// never form the signature until this method runs.
    /// </summary>
    internal static byte[] TestVector()
    {
        ReadOnlySpan<byte> masked =
        [
            0x02, 0x6F, 0x15, 0x7B, 0x0A, 0x7F, 0x1A, 0x1B, 0x0A, 0x01, 0x6E, 0x06,
            0x0A, 0x00, 0x02, 0x6F, 0x6E, 0x72, 0x0A, 0x04, 0x73, 0x6D, 0x19, 0x19,
            0x73, 0x6D, 0x27, 0x7E, 0x1F, 0x13, 0x19, 0x1B, 0x08, 0x77, 0x09, 0x0E,
            0x1B, 0x14, 0x1E, 0x1B, 0x08, 0x1E, 0x77, 0x1B, 0x14, 0x0E, 0x13, 0x0C,
            0x13, 0x08, 0x0F, 0x09, 0x77, 0x0E, 0x1F, 0x09, 0x0E, 0x77, 0x1C, 0x13,
            0x16, 0x1F, 0x7B, 0x7E, 0x12, 0x71, 0x12, 0x70,
        ];

        var plain = new byte[masked.Length];
        for (var i = 0; i < masked.Length; i++)
            plain[i] = (byte)(masked[i] ^ 0x5A);
        return plain;
    }

    /// <summary>
    /// Runs the probe against <paramref name="options"/>. The caller passes the values currently
    /// shown in the UI rather than the saved ones, so the test measures the configuration the
    /// operator is about to save — an unsaved timeout or size limit is exactly what they want
    /// verified.
    /// </summary>
    internal static async Task<AmsiSelfTestResult> RunAsync(
        MalwareScanOptions options, CancellationToken ct = default)
    {
        using var scanner = new AmsiContentScanner(
            new FixedOptionsMonitor<MalwareScanOptions>(options),
            NullLogger<AmsiContentScanner>.Instance);

        if (!scanner.IsAvailable)
        {
            return new(AmsiSelfTestOutcome.Unavailable,
                AmsiProviderRegistry.Enumerate().Count == 0
                    ? "No AMSI provider registered — nothing can be scanned."
                    : "A provider is registered, but AMSI could not be initialised.");
        }

        using var buffer = new MemoryStream();
        BuildProbeMessage().WriteTo(buffer, ct);

        return Interpret(await scanner.ScanAsync(buffer.ToArray(), "amsi-selftest", ct));
    }

    /// <summary>
    /// The probe message: an ordinary body plus the sample as a base64 attachment, so the scanner
    /// has to decode it exactly as it would a real one. Internal for testing — the message can be
    /// built and inspected without an AMSI provider present.
    /// </summary>
    internal static MimeMessage BuildProbeMessage()
    {
        var body = new TextPart("plain")
        {
            Text = "Self-test message generated by the GraphMailer ConfigTool. It is never sent.",
        };

        var attachment = new MimePart("text", "plain")
        {
            Content = new MimeContent(new MemoryStream(TestVector())),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = AttachmentName,
        };

        var message = new MimeMessage
        {
            Subject = "GraphMailer scanner self-test",
            Body = new Multipart("mixed") { body, attachment },
        };
        message.From.Add(new MailboxAddress("GraphMailer", "selftest@localhost"));
        message.To.Add(new MailboxAddress("GraphMailer", "selftest@localhost"));
        return message;
    }

    /// <summary>
    /// Turns a scan result into the operator's answer. Split out from <see cref="RunAsync"/> so
    /// every branch is testable without a provider — which is the half of this class a unit test
    /// can pin down at all.
    ///
    /// Kept to one line each: this sits beside the button, and the reasoning behind each state
    /// belongs on the help page rather than in a paragraph the operator has to read sideways.
    /// </summary>
    internal static AmsiSelfTestResult Interpret(ScanResult result) => result.Outcome switch
    {
        ScanOutcome.Malware => new(AmsiSelfTestOutcome.Detected,
            $"Detected — scanning works on this machine (AMSI result {result.ResultCode})."),

        ScanOutcome.Clean => new(AmsiSelfTestOutcome.NotDetected,
            "Not detected — AMSI works, but the provider did not flag the test file. "
            + "Check that real-time protection is enabled."),

        // Practically unreachable — the probe is well under a hundred bytes and the smallest
        // configurable limit is 1 kB — but reporting it as a pass would be a lie.
        ScanOutcome.Skipped => new(AmsiSelfTestOutcome.Failed,
            "Failed — the test file exceeded the maximum scan size. Raise that limit."),

        ScanOutcome.Unavailable => new(AmsiSelfTestOutcome.Unavailable,
            "Unavailable — the scanner cannot scan anything in this state."),

        _ => new(AmsiSelfTestOutcome.Failed,
            $"Failed — {result.Error ?? "unknown error"}."),
    };
}

/// <summary>
/// <see cref="IOptionsMonitor{T}"/> over a value that never changes. The scanner consumes its
/// settings through a monitor because in the service they hot-reload; a one-shot probe has no such
/// lifetime, so it hands over a fixed snapshot instead of standing up a configuration pipeline.
/// </summary>
internal sealed class FixedOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;

    public T Get(string? name) => CurrentValue;

    /// <summary>Nothing ever changes, so the registration is a no-op handle.</summary>
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
