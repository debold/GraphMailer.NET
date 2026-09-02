using GraphMailer.Service.Services;

namespace GraphMailer.Tests.Unit.Services;

/// <summary>
/// The file the service writes so the ConfigTool — a separate process — can show what the last
/// tenant sync recognised. Purely informational, so it must never throw its way into the sync
/// path, and a half-written or corrupt file has to read back as "nothing yet".
/// </summary>
public sealed class SenderDirectorySnapshotTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "gm-snapshot-tests-" + Guid.NewGuid().ToString("N"));

    public SenderDirectorySnapshotTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    private static readonly DateTime Generated = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    private static TenantUser Mailbox(string upn, params string[] aliases) =>
        new("id-" + upn, upn, true, [upn, .. aliases], TenantRecipientKind.Mailbox, "Display " + upn);

    private static TenantUser Group(string mail) =>
        new("id-" + mail, mail, true, [mail], TenantRecipientKind.Group, "Group " + mail);

    [Fact]
    public void From_CarriesKindNameAddressesAndAliases()
    {
        var snapshot = SenderDirectorySnapshot.From(
            [Mailbox("alice@corp.com", "a.alias@corp.com")], [], Generated);

        var entry = snapshot.Entries.Should().ContainSingle().Subject;
        entry.Kind.Should().Be("Mailbox");
        entry.DisplayName.Should().Be("Display alice@corp.com");
        entry.PrimaryAddress.Should().Be("alice@corp.com");
        entry.Addresses.Should().BeEquivalentTo(["alice@corp.com", "a.alias@corp.com"]);
    }

    [Fact]
    public void From_GroupsAreLabelledAsSuch()
    {
        var snapshot = SenderDirectorySnapshot.From([Group("sales@corp.com")], [], Generated);

        snapshot.Entries.Should().ContainSingle().Which.Kind.Should().Be("Group");
    }

    [Fact]
    public void From_StampsTheGenerationTime()
        => SenderDirectorySnapshot.From([], [], Generated).GeneratedUtc.Should().Be(Generated);

    [Fact]
    public void From_SortsMailboxesBeforeGroupsThenByName()
    {
        var snapshot = SenderDirectorySnapshot.From(
            [Group("zulu@corp.com"), Mailbox("bob@corp.com"), Mailbox("anna@corp.com")], [], Generated);

        snapshot.Entries.Select(e => e.Kind)
            .Should().Equal(["Mailbox", "Mailbox", "Group"]);
        snapshot.Entries.Take(2).Select(e => e.PrimaryAddress)
            .Should().Equal(["anna@corp.com", "bob@corp.com"]);
    }

    [Fact]
    public void From_NotTruncated_ForAnOrdinaryTenant()
        => SenderDirectorySnapshot.From([Mailbox("a@corp.com")], [], Generated).Truncated.Should().BeFalse();

    [Fact]
    public void SaveAndLoad_RoundTripsEveryField()
    {
        var path = Path_("sender-directory.json");
        SenderDirectorySnapshot
            .From([Mailbox("alice@corp.com", "a.alias@corp.com"), Group("sales@corp.com")], [], Generated)
            .Save(path);

        var loaded = SenderDirectorySnapshot.TryLoad(path);

        loaded.Should().NotBeNull();
        loaded!.GeneratedUtc.Should().Be(Generated);
        loaded.Entries.Should().HaveCount(2);
        loaded.Entries[0].Addresses.Should().BeEquivalentTo(["alice@corp.com", "a.alias@corp.com"]);
    }

    [Fact]
    public void TryLoad_MissingFile_ReturnsNull()
        => SenderDirectorySnapshot.TryLoad(Path_("nope.json")).Should().BeNull();

    [Fact]
    public void TryLoad_CorruptFile_ReturnsNullInsteadOfThrowing()
    {
        var path = Path_("broken.json");
        File.WriteAllText(path, "{ this is not json");

        SenderDirectorySnapshot.TryLoad(path).Should().BeNull();
    }

    [Fact]
    public void Save_CreatesTheDirectoryWhenMissing()
    {
        var path = Path.Combine(_dir, "nested", "sender-directory.json");

        SenderDirectorySnapshot.From([Mailbox("a@corp.com")], [], Generated).Save(path);

        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void From_CarriesTheDerivedMailDomains_Sorted()
    {
        var snapshot = SenderDirectorySnapshot.From(
            [Mailbox("a@corp.com")], ["@zulu.com", "@corp.com"], Generated);

        snapshot.Domains.Should().Equal(["@corp.com", "@zulu.com"]);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsTheDomains()
    {
        var path = Path_("with-domains.json");
        SenderDirectorySnapshot
            .From([Mailbox("a@corp.com")], ["@corp.com", "@corp.de"], Generated)
            .Save(path);

        SenderDirectorySnapshot.TryLoad(path)!.Domains
            .Should().BeEquivalentTo(["@corp.com", "@corp.de"]);
    }

    [Fact]
    public void From_NoDomains_LeavesTheListEmptyRatherThanNull()
        => SenderDirectorySnapshot.From([], [], Generated).Domains.Should().BeEmpty();
}
