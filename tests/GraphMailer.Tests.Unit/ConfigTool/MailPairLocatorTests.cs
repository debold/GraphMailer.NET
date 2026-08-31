using GraphMailer.ConfigTool.Helpers;

namespace GraphMailer.Tests.Unit.ConfigTool;

/// <summary>
/// Pairing a stored message with its metadata sidecar.
///
/// Half a pair is a normal thing to find — the two files are written separately, and a folder can
/// hold an orphan from an interrupted write or a file copied on its own. Every case therefore has
/// to yield what it does hold rather than being refused.
///
/// The string handling is the other reason these exist: <c>Path.ChangeExtension</c> on
/// <c>x.meta.json</c> yields <c>x.meta</c>, not <c>x</c>, which is exactly the kind of near-miss
/// that produces a lookup for a file that can never exist.
/// </summary>
public sealed class MailPairLocatorTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "mailpair-tests-" + Guid.NewGuid().ToString("N"));

    public MailPairLocatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string Write(string name, string content = "x")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    // =========================================================================
    // Name derivation
    // =========================================================================

    [Fact]
    public void MetaPathFor_MessageFile_AppendsTheSidecarSuffix()
    {
        MailPairLocator.MetaPathFor(@"C:\mail\queue\abc123.eml")
            .Should().Be(@"C:\mail\queue\abc123.meta.json");
    }

    [Fact]
    public void EmlPathFor_Sidecar_StripsTheWholeDoubleExtension()
    {
        // The near-miss this guards: a naive ChangeExtension leaves "abc123.meta".
        MailPairLocator.EmlPathFor(@"C:\mail\queue\abc123.meta.json")
            .Should().Be(@"C:\mail\queue\abc123.eml");
    }

    [Theory]
    [InlineData(@"C:\mail\abc.EML", @"C:\mail\abc.meta.json")]
    [InlineData(@"C:\mail\abc.Eml", @"C:\mail\abc.meta.json")]
    public void MetaPathFor_IsCaseInsensitiveOnTheSuffix(string input, string expected)
    {
        MailPairLocator.MetaPathFor(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"C:\mail\abc.txt")]
    [InlineData(@"C:\mail\abc.meta.json")]
    [InlineData("")]
    [InlineData(null)]
    public void MetaPathFor_SomethingOtherThanAMessage_IsNull(string? input)
    {
        MailPairLocator.MetaPathFor(input).Should().BeNull();
    }

    [Theory]
    [InlineData(@"C:\mail\abc.eml")]
    [InlineData(@"C:\mail\abc.json")]
    [InlineData("")]
    [InlineData(null)]
    public void EmlPathFor_SomethingOtherThanASidecar_IsNull(string? input)
    {
        MailPairLocator.EmlPathFor(input).Should().BeNull();
    }

    // =========================================================================
    // Resolving what is actually on disk
    // =========================================================================

    [Fact]
    public void Resolve_CompletePair_FindsBothHalvesFromEitherSide()
    {
        var eml = Write("abc.eml");
        var meta = Write("abc.meta.json", "{}");

        var fromEml = MailPairLocator.Resolve(eml);
        fromEml.EmlPath.Should().Be(eml);
        fromEml.MetaPath.Should().Be(meta);

        var fromMeta = MailPairLocator.Resolve(meta);
        fromMeta.EmlPath.Should().Be(eml);
        fromMeta.MetaPath.Should().Be(meta);
    }

    [Fact]
    public void Resolve_MessageWithoutASidecar_StillYieldsTheMessage()
    {
        var eml = Write("orphan.eml");

        var pair = MailPairLocator.Resolve(eml);

        pair.HasMessage.Should().BeTrue();
        pair.EmlPath.Should().Be(eml);
        pair.HasMetadata.Should().BeFalse("the envelope then has to come from the headers");
    }

    [Fact]
    public void Resolve_SidecarWithoutAMessage_StillYieldsTheSidecar()
    {
        // Refusing this would throw away the one thing the sidecar is good for: the envelope,
        // which cannot be derived from a message at all.
        var meta = Write("orphan.meta.json", "{}");

        var pair = MailPairLocator.Resolve(meta);

        pair.HasMetadata.Should().BeTrue();
        pair.MetaPath.Should().Be(meta);
        pair.HasMessage.Should().BeFalse();
    }

    [Fact]
    public void Resolve_UnrelatedFile_IsTakenAsAMessage()
    {
        // The dialog's "all files" filter allows anything; whether it parses as mail is the
        // parser's answer to give, not this method's.
        var other = Write("message.txt");

        var pair = MailPairLocator.Resolve(other);

        pair.EmlPath.Should().Be(other);
        pair.HasMetadata.Should().BeFalse();
    }

    [Fact]
    public void Resolve_SidecarOfADifferentMessage_IsNotPairedUp()
    {
        // Pairing is by exact stem; a neighbouring message must not be adopted.
        var eml = Write("aaa.eml");
        Write("bbb.meta.json", "{}");

        MailPairLocator.Resolve(eml).HasMetadata.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Resolve_NoPath_YieldsNeitherHalf(string? input)
    {
        var pair = MailPairLocator.Resolve(input);

        pair.HasMessage.Should().BeFalse();
        pair.HasMetadata.Should().BeFalse();
    }
}
