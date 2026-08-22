using System.Security.Cryptography;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Security.Amsi;

namespace GraphMailer.Tests.Unit.Infrastructure.Security.Amsi;

/// <summary>
/// Covers the scanner self-test behind the ConfigTool's "Test scanner" button, minus the native
/// call: how a scan result becomes the operator's answer, and that the probe is actually shaped
/// like the attachment the scanner is supposed to look inside. The native round-trip is the
/// opt-in test in <see cref="AmsiContentScannerTests"/>, which scans this same vector.
/// </summary>
public sealed class AmsiSelfTestTests
{
    // =========================================================================
    // Test vector
    // =========================================================================

    /// <summary>
    /// Pins the vector by hash rather than by literal: a hash is not a signature, so this
    /// assertion can be written down without putting the test file's bytes into the source —
    /// which is the whole reason the vector is XOR-masked in the first place.
    /// </summary>
    [Fact]
    public void TestVector_IsTheStandardAntivirusTestFile()
    {
        var hash = Convert.ToHexString(SHA256.HashData(AmsiSelfTest.TestVector())).ToLowerInvariant();

        hash.Should().Be("275a021bbfb6489e54d471899f7db9d1663fc695ec2fe2a2c4538aabf651fd0f");
    }

    [Fact]
    public void TestVector_ReturnsAFreshArrayEachCall()
    {
        // The scanner hands buffers to native code; a shared array would be a surprise waiting
        // to happen if a caller ever mutated it.
        var first = AmsiSelfTest.TestVector();
        var second = AmsiSelfTest.TestVector();

        first.Should().NotBeSameAs(second);
        first.Should().Equal(second);
    }

    // =========================================================================
    // Probe message
    // =========================================================================

    /// <summary>
    /// The probe only proves anything if the scanner classifies it the way it classifies real
    /// mail: as an attachment, which is the path that decodes before scanning and the only one
    /// that produces an allowlistable hash.
    /// </summary>
    [Fact]
    public void BuildProbeMessage_ExposesTheVectorAsADecodableAttachment()
    {
        var targets = AmsiContentScanner
            .EnumerateTargets(AmsiSelfTest.BuildProbeMessage(), depth: 0)
            .ToList();

        var attachment = targets.Should().ContainSingle(t => t.IsAttachment).Subject;
        attachment.Name.Should().Be(AmsiSelfTest.AttachmentName);
        attachment.Load().Should().Equal(AmsiSelfTest.TestVector(),
            "the scanner must see the decoded bytes, not their base64 form");
    }

    [Fact]
    public void BuildProbeMessage_IsNeverAddressedOutsideTheMachine()
    {
        // The probe is scanned in-process and never queued, but an address that could leave the
        // host would make a stray send a real incident rather than a harmless one.
        var message = AmsiSelfTest.BuildProbeMessage();

        message.To.Mailboxes.Should().OnlyContain(m => m.Address.EndsWith("@localhost"));
        message.From.Mailboxes.Should().OnlyContain(m => m.Address.EndsWith("@localhost"));
    }

    // =========================================================================
    // Result interpretation
    // =========================================================================

    [Fact]
    public void Interpret_Detection_ReportsSuccessAndNamesTheResultCode()
    {
        var result = AmsiSelfTest.Interpret(
            new ScanResult(ScanOutcome.Malware, AmsiSelfTest.AttachmentName, "abc", 68, 32768));

        result.Outcome.Should().Be(AmsiSelfTestOutcome.Detected);
        result.Detail.Should().Contain("32768");
    }

    /// <summary>
    /// The subtle failure: AMSI works, a provider is registered, and it still waves the test file
    /// through. That is not a pass — reporting it as one would tell an operator their mail is
    /// being scanned when it is not.
    /// </summary>
    [Fact]
    public void Interpret_CleanProbe_IsNotReportedAsSuccess()
    {
        var result = AmsiSelfTest.Interpret(ScanResult.Clean());

        result.Outcome.Should().Be(AmsiSelfTestOutcome.NotDetected);
        result.Detail.Should().Contain("real-time protection");
    }

    [Fact]
    public void Interpret_SkippedBySizeLimit_IsAFailureThatNamesTheLimit()
    {
        var result = AmsiSelfTest.Interpret(ScanResult.Skipped(AmsiSelfTest.AttachmentName, 68));

        result.Outcome.Should().Be(AmsiSelfTestOutcome.Failed);
        result.Detail.Should().Contain("maximum scan size");
    }

    [Fact]
    public void Interpret_Unavailable_ReportsUnavailable()
        => AmsiSelfTest.Interpret(ScanResult.Unavailable())
            .Outcome.Should().Be(AmsiSelfTestOutcome.Unavailable);

    [Fact]
    public void Interpret_Failure_CarriesTheUnderlyingError()
    {
        var result = AmsiSelfTest.Interpret(ScanResult.Failed("scan timed out after 30s"));

        result.Outcome.Should().Be(AmsiSelfTestOutcome.Failed);
        result.Detail.Should().Contain("scan timed out after 30s");
    }

    [Fact]
    public void Interpret_EveryOutcome_ProducesAnExplanation()
    {
        // The detail text is the entire answer the operator gets; an empty one would leave the
        // button reporting a colour and nothing else.
        foreach (var outcome in Enum.GetValues<ScanOutcome>())
            AmsiSelfTest.Interpret(new ScanResult(outcome)).Detail.Should().NotBeNullOrWhiteSpace();
    }

    // =========================================================================
    // Options snapshot
    // =========================================================================

    [Fact]
    public void FixedOptionsMonitor_ReturnsTheSnapshotForEveryName()
    {
        var options = new MalwareScanOptions { TimeoutSeconds = 7 };
        var monitor = new FixedOptionsMonitor<MalwareScanOptions>(options);

        monitor.CurrentValue.Should().BeSameAs(options);
        monitor.Get("anything").Should().BeSameAs(options);
        monitor.OnChange((_, _) => { }).Should().BeNull("a fixed snapshot never raises a change");
    }
}
