using System.Text;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Security.Amsi;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using NSubstitute;

namespace GraphMailer.Tests.Unit.Infrastructure.Security.Amsi;

/// <summary>
/// Covers the half of the AMSI scanner a unit test can pin down without a live antimalware
/// provider: the result semantics, the provider enumeration, and how a message is walked into
/// scannable parts. The native round-trip is exercised by the opt-in test at the bottom.
/// </summary>
public sealed class AmsiContentScannerTests
{
    private static IOptionsMonitor<T> Monitor<T>(T value)
    {
        var monitor = Substitute.For<IOptionsMonitor<T>>();
        monitor.CurrentValue.Returns(value);
        return monitor;
    }

    // =========================================================================
    // AMSI result semantics
    // =========================================================================

    [Theory]
    [InlineData(0u, false)]        // AMSI_RESULT_CLEAN
    [InlineData(1u, false)]        // AMSI_RESULT_NOT_DETECTED
    [InlineData(16383u, false)]    // just below the admin-block range
    [InlineData(16384u, true)]     // AMSI_RESULT_BLOCKED_BY_ADMIN_START
    [InlineData(20479u, true)]     // AMSI_RESULT_BLOCKED_BY_ADMIN_END
    [InlineData(32768u, true)]     // AMSI_RESULT_DETECTED
    [InlineData(40000u, true)]     // provider-specific value above DETECTED
    public void IsMalware_MatchesTheAmsiResultIsMalwareMacro(uint result, bool expected)
        => AmsiResult.IsMalware(result).Should().Be(expected);

    [Fact]
    public void ScanResult_AttachmentDetectionWithHash_IsAllowlistable()
        => new ScanResult(ScanOutcome.Malware, "invoice.docm", "abc", 10, 32768)
            .IsAllowlistable.Should().BeTrue();

    [Fact]
    public void ScanResult_BodyDetectionWithoutHash_IsNotAllowlistable()
    {
        // A message body differs on every mail, so an allowlist entry for it could never
        // match again — the absent hash is what encodes that.
        new ScanResult(ScanOutcome.Malware, "message body (html)", null, 10, 32768)
            .IsAllowlistable.Should().BeFalse();
    }

    [Fact]
    public void ScanResult_CleanOutcome_IsNotAllowlistable()
        => ScanResult.Clean().IsAllowlistable.Should().BeFalse();

    // =========================================================================
    // Provider registry
    // =========================================================================

    [Fact]
    public void Enumerate_ReturnsWellFormedEntriesAndNeverThrows()
    {
        // Machine-dependent: a build agent without antivirus legitimately returns nothing.
        // What must hold either way is that enumeration never throws — every failure mode
        // has to collapse into "no provider", which is what disables scanning.
        var providers = AmsiProviderRegistry.Enumerate();

        providers.Should().NotBeNull();
        providers.Should().OnlyContain(p => !string.IsNullOrWhiteSpace(p.Clsid));
    }

    [Fact]
    public void Describe_WithNameAndDll_UsesTheFileNameOnly()
        => new AmsiProvider("{clsid}", "Windows Defender", @"C:\Program Files\x\MpOav.dll")
            .Describe().Should().Be("Windows Defender (MpOav.dll)");

    [Fact]
    public void Describe_WithoutName_FallsBackToTheClsid()
        => new AmsiProvider("{clsid}", "", "").Describe().Should().Be("{clsid} (unknown DLL)");

    // =========================================================================
    // Message walking
    // =========================================================================

    [Fact]
    public void EnumerateTargets_PlainTextMessage_YieldsOneBodyTarget()
    {
        var message = new MimeMessage { Body = new TextPart("plain") { Text = "hello" } };

        var targets = AmsiContentScanner.EnumerateTargets(message, depth: 0).ToList();

        targets.Should().ContainSingle();
        targets[0].IsAttachment.Should().BeFalse();
        targets[0].Name.Should().Contain("message body");
        Encoding.UTF8.GetString(targets[0].Load()).Should().Be("hello");
    }

    [Fact]
    public void EnumerateTargets_HtmlAndTextBody_YieldsBoth()
    {
        var body = new Multipart("alternative")
        {
            new TextPart("plain") { Text = "text version" },
            new TextPart("html") { Text = "<p>html version</p>" },
        };
        var message = new MimeMessage { Body = body };

        var targets = AmsiContentScanner.EnumerateTargets(message, depth: 0).ToList();

        targets.Should().HaveCount(2);
        targets.Should().OnlyContain(t => !t.IsAttachment);
    }

    [Fact]
    public void EnumerateTargets_Attachment_IsLoadedDecoded()
    {
        // The whole point of walking parts: a base64 attachment must reach the provider as
        // its decoded bytes, because no signature matches base64 text.
        var payload = "the decoded payload"u8.ToArray();
        var attachment = new MimePart("application", "octet-stream")
        {
            FileName = "payload.bin",
            Content = new MimeContent(new MemoryStream(payload)),
            ContentTransferEncoding = ContentEncoding.Base64,
        };
        var message = new MimeMessage
        {
            Body = new Multipart("mixed") { new TextPart("plain") { Text = "see attached" }, attachment },
        };

        var targets = AmsiContentScanner.EnumerateTargets(message, depth: 0).ToList();

        var found = targets.Should().ContainSingle(t => t.IsAttachment).Subject;
        found.Name.Should().Be("payload.bin");
        found.Load().Should().Equal(payload);
    }

    [Fact]
    public void EnumerateTargets_NestedRfc822_WalksTheInnerMessage()
    {
        // Delivery keeps an attached mail opaque; scanning must not, or the inner mail's
        // attachments would only ever be seen base64-encoded.
        var inner = new MimeMessage { Body = BuildAttachmentBody("inner.exe", "inner payload"u8.ToArray()) };
        var message = new MimeMessage
        {
            Body = new Multipart("mixed")
            {
                new TextPart("plain") { Text = "forwarded" },
                new MessagePart { Message = inner },
            },
        };

        var targets = AmsiContentScanner.EnumerateTargets(message, depth: 0).ToList();

        targets.Should().Contain(t => t.Name == "inner.exe" && t.IsAttachment);
    }

    [Fact]
    public void EnumerateTargets_AtTheDepthLimit_ScansTheAttachedMessageWhole()
    {
        // Degraded, never ignored: at the limit the nested mail is handed over as one blob
        // instead of being walked, so a crafted nesting chain cannot smuggle content past.
        var inner = new MimeMessage { Body = BuildAttachmentBody("inner.exe", "inner payload"u8.ToArray()) };
        var message = new MimeMessage
        {
            Body = new Multipart("mixed") { new MessagePart { Message = inner } },
        };

        var targets = AmsiContentScanner.EnumerateTargets(message, depth: 5).ToList();

        targets.Should().ContainSingle();
        targets[0].Name.Should().Be("attached message");
        targets[0].Load().Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void EnumerateTargets_AttachmentWithoutFileName_StillGetsAName()
    {
        var message = new MimeMessage { Body = BuildAttachmentBody(fileName: null, "data"u8.ToArray()) };

        var targets = AmsiContentScanner.EnumerateTargets(message, depth: 0).ToList();

        targets.Should().ContainSingle(t => t.IsAttachment)
            .Which.Name.Should().NotBeNullOrWhiteSpace("AMSI needs a content name for every buffer");
    }

    private static MimeEntity BuildAttachmentBody(string? fileName, byte[] payload)
    {
        var part = new MimePart("application", "octet-stream")
        {
            Content = new MimeContent(new MemoryStream(payload)),
            ContentTransferEncoding = ContentEncoding.Base64,
        };
        if (fileName is not null) part.FileName = fileName;
        return new Multipart("mixed") { part };
    }

    // =========================================================================
    // Availability
    // =========================================================================

    [Fact]
    public void Scanner_WhenUnavailable_ReturnsUnavailableWithoutTouchingTheMessage()
    {
        // Constructed on whatever machine runs the suite. If no provider is registered the
        // scanner must report Unavailable rather than a clean verdict — the caller relies on
        // that distinction to avoid reporting unscanned mail as scanned.
        using var scanner = new AmsiContentScanner(
            Monitor(new MalwareScanOptions()), NullLogger<AmsiContentScanner>.Instance);

        if (scanner.IsAvailable)
        {
            scanner.Providers.Should().NotBeEmpty("availability implies at least one provider");
            return;
        }

        scanner.Providers.Should().BeEmpty();
    }

    // =========================================================================
    // Opt-in: real AMSI round-trip
    // =========================================================================

    /// <summary>
    /// The test vector itself lives in <see cref="AmsiSelfTest"/>, because the ConfigTool's
    /// scanner self-test scans the very same bytes. Keeping one copy means this round-trip proves
    /// what that button relies on, rather than two masked blobs drifting apart.
    /// </summary>
    private static byte[] BuildAntivirusTestVector() => AmsiSelfTest.TestVector();

    /// <summary>
    /// End-to-end proof that the P/Invoke layer, the session handling and the part walking
    /// actually reach a real provider — nothing else in this suite exercises the native path.
    ///
    /// Opt-in by design: a detection is a genuine antivirus event, logged against this process
    /// (Defender writes event 1116 with the calling process name). Running it on every
    /// <c>dotnet test</c> would produce a malware alert per test run on the developer's machine
    /// and, on a managed endpoint, an alert in the security portal.
    /// </summary>
    [Fact]
    public async Task ScanAsync_AntivirusTestVectorAsAttachment_IsDetected()
    {
        // Self-skipping like the live tests: set GRAPHMAILER_AMSI_LIVE_TEST=1 to run it.
        if (Environment.GetEnvironmentVariable("GRAPHMAILER_AMSI_LIVE_TEST") != "1") return;

        using var scanner = new AmsiContentScanner(
            Monitor(new MalwareScanOptions()), NullLogger<AmsiContentScanner>.Instance);
        if (!scanner.IsAvailable) return;   // no provider on this machine — nothing to prove

        var message = new MimeMessage
        {
            Subject = "amsi round-trip",
            Body = BuildAttachmentBody("sample.txt", BuildAntivirusTestVector()),
        };
        message.From.Add(new MailboxAddress("s", "s@example.com"));
        message.To.Add(new MailboxAddress("r", "r@example.com"));

        using var raw = new MemoryStream();
        await message.WriteToAsync(raw);

        var result = await scanner.ScanAsync(raw.ToArray(), "amsi-roundtrip");

        result.Outcome.Should().Be(ScanOutcome.Malware);
        result.ThreatLocation.Should().Be("sample.txt");
        result.Sha256.Should().NotBeNullOrEmpty("an attachment detection must be allowlistable");
    }
}
