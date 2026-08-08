using System.Text.Json;
using GraphMailer.ConfigTool.Services;
using GraphMailer.Service.Services;

namespace GraphMailer.Tests.Unit.ConfigTool;

/// <summary>
/// Tests for <see cref="MailFolderReader"/> — the directory scanner behind the
/// ConfigTool Messages page (queue / failed / sent browser).
/// </summary>
public sealed class MailFolderReaderTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "mailfolder-tests-" + Guid.NewGuid().ToString("N"));

    public MailFolderReaderTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private void WriteMeta(string messageId, MailMetadata meta)
        => File.WriteAllText(
            Path.Combine(_dir, $"{messageId}.meta.json"),
            JsonSerializer.Serialize(meta));

    /// <summary>One full page, unfiltered — what the page shows before any paging or search.</summary>
    private static List<MessageRow> Read(string directory)
        => MailFolderReader.ReadFolder(directory, MailFolderReader.PageSize, null).Rows;

    private static List<MessageRow> ReadMerged(params string[] directories)
        => MailFolderReader.ReadFolders(MailFolderReader.PageSize, null, directories).Rows;

    // =========================================================================

    [Fact]
    public void ReadFolder_MissingDirectory_ReturnsEmpty()
    {
        Read(Path.Combine(_dir, "does-not-exist")).Should().BeEmpty();
    }

    [Fact]
    public void ReadFolder_MapsAllFields()
    {
        var received = new DateTime(2026, 6, 11, 10, 0, 0, DateTimeKind.Utc);
        var lastAttempt = received.AddMinutes(5);
        var nextRetry = received.AddMinutes(10);
        WriteMeta("m1", new MailMetadata
        {
            MessageId = "m1",
            From = "sender@corp.com",
            To = ["a@x.com", "b@x.com"],
            Subject = "Hello",
            Status = "queued",
            ReceivedAt = received,
            ClientIp = "10.0.0.5",
            SmtpMessageId = "<abc@corp.com>",
            RetryCount = 2,
            LastAttemptAt = lastAttempt,
            LastError = "HTTP 404 ErrorInvalidUser",
            NextRetryAt = nextRetry,
            SentAt = received.AddMinutes(15),
        });

        var rows = Read(_dir);

        rows.Should().ContainSingle();
        var row = rows[0];
        row.MessageId.Should().Be("m1");
        row.From.Should().Be("sender@corp.com");
        row.To.Should().Be("a@x.com, b@x.com");
        row.Subject.Should().Be("Hello");
        row.Status.Should().Be("queued");
        row.Attempts.Should().Be("2");
        row.LastError.Should().Be("HTTP 404 ErrorInvalidUser");
        row.ClientIp.Should().Be("10.0.0.5");
        row.SmtpMessageId.Should().Be("<abc@corp.com>");
        row.ReceivedAt.Should().Be(received.ToLocalTime());
        row.LastAttemptAt.Should().Be(lastAttempt.ToLocalTime());
        row.NextRetryAt.Should().Be(nextRetry.ToLocalTime());
        row.SentAt.Should().Be(received.AddMinutes(15).ToLocalTime());
    }

    [Fact]
    public void ReadFolder_MetaWithoutOptionalFields_MapsToEmptyValues()
    {
        // Older meta files (written before LastAttemptAt/LastError existed)
        WriteMeta("m1", new MailMetadata { MessageId = "m1", From = "s@x.com", ReceivedAt = DateTime.UtcNow });

        var rows = Read(_dir);

        rows.Should().ContainSingle();
        rows[0].LastAttemptAt.Should().BeNull();
        rows[0].LastError.Should().Be(string.Empty);
        rows[0].NextRetryAt.Should().BeNull();
        rows[0].Attempts.Should().Be("0");
    }

    [Fact]
    public void ReadFolder_SentMessage_AttemptsIncludeTheSuccessfulTry()
    {
        // RetryCount counts failed attempts; a first-try delivery must read "1"
        WriteMeta("ok", new MailMetadata
        {
            MessageId = "ok", Status = "sent", RetryCount = 0, ReceivedAt = DateTime.UtcNow,
        });
        // delivered on the 3rd try (2 failures) must read "3"
        WriteMeta("retried", new MailMetadata
        {
            MessageId = "retried", Status = "sent", RetryCount = 2, ReceivedAt = DateTime.UtcNow,
        });

        var rows = Read(_dir);

        rows.Single(r => r.MessageId == "ok").Attempts.Should().Be("1");
        rows.Single(r => r.MessageId == "retried").Attempts.Should().Be("3");
    }

    [Fact]
    public void ReadFolder_SortsByReceivedAtDescending()
    {
        var t0 = new DateTime(2026, 6, 11, 8, 0, 0, DateTimeKind.Utc);
        WriteMeta("old", new MailMetadata { MessageId = "old", Subject = "old", ReceivedAt = t0 });
        WriteMeta("new", new MailMetadata { MessageId = "new", Subject = "new", ReceivedAt = t0.AddHours(2) });
        WriteMeta("mid", new MailMetadata { MessageId = "mid", Subject = "mid", ReceivedAt = t0.AddHours(1) });

        var rows = Read(_dir);

        rows.Select(r => r.Subject).Should().ContainInOrder("new", "mid", "old");
    }

    [Fact]
    public void ReadFolder_CorruptFile_IsSkipped()
    {
        WriteMeta("good", new MailMetadata { MessageId = "good", Subject = "ok", ReceivedAt = DateTime.UtcNow });
        File.WriteAllText(Path.Combine(_dir, "broken.meta.json"), "{ not valid json !!!");

        var rows = Read(_dir);

        rows.Should().ContainSingle().Which.Subject.Should().Be("ok");
    }

    [Fact]
    public void ReadFolder_MoreThanMaxEntries_ReturnsNewestCapped()
    {
        var t0 = new DateTime(2026, 6, 11, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < MailFolderReader.MaxEntries + 5; i++)
            WriteMeta($"m{i:D4}", new MailMetadata
            {
                MessageId = $"m{i:D4}",
                Subject = $"s{i:D4}",
                ReceivedAt = t0.AddSeconds(i),
            });

        var rows = Read(_dir);

        rows.Should().HaveCount(MailFolderReader.MaxEntries);
        // the cap must keep the NEWEST entries and drop the oldest
        rows[0].Subject.Should().Be($"s{MailFolderReader.MaxEntries + 4:D4}");
        rows[^1].Subject.Should().Be("s0005");
    }

    // ── "All" folder: queue + failed + sent merged into one list ──────────────

    private string SubDir(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteMetaTo(string directory, string messageId, MailMetadata meta)
        => File.WriteAllText(
            Path.Combine(directory, $"{messageId}.meta.json"),
            JsonSerializer.Serialize(meta));

    [Fact]
    public void ReadFolders_MergesAllFoldersNewestFirst()
    {
        var t0 = new DateTime(2026, 6, 11, 8, 0, 0, DateTimeKind.Utc);
        var queue = SubDir("queue");
        var failed = SubDir("failed");
        var sent = SubDir("sent");

        WriteMetaTo(queue, "q1", new MailMetadata { MessageId = "q1", Subject = "queued", Status = "queued", ReceivedAt = t0.AddHours(1) });
        WriteMetaTo(failed, "f1", new MailMetadata { MessageId = "f1", Subject = "failed", Status = "failed", ReceivedAt = t0.AddHours(2) });
        WriteMetaTo(sent, "s1", new MailMetadata { MessageId = "s1", Subject = "sent", Status = "sent", ReceivedAt = t0 });

        var rows = ReadMerged(queue, failed, sent);

        rows.Select(r => r.Subject).Should().ContainInOrder(
            new[] { "failed", "queued", "sent" },
            "the merged list is ordered by receipt time, not by folder");
        rows.Select(r => r.Status).Should().Contain(["queued", "failed", "sent"],
            "the status column is what tells the folders apart in the merged view");
    }

    [Fact]
    public void ReadFolders_MissingFolder_IsSkipped()
    {
        var queue = SubDir("queue");
        WriteMetaTo(queue, "q1", new MailMetadata { MessageId = "q1", Subject = "queued", ReceivedAt = DateTime.UtcNow });

        var rows = ReadMerged(queue, Path.Combine(_dir, "failed"), Path.Combine(_dir, "sent"));

        rows.Should().ContainSingle().Which.Subject.Should().Be("queued",
            "sent/failed do not exist until the service has written to them");
    }

    [Theory]
    [InlineData("queued", "Queued")]
    [InlineData("sent", "Sent")]
    [InlineData("failed", "Failed")]
    [InlineData("", "—")]
    public void StatusPill_LabelIsCapitalised(string status, string expected)
    {
        new MessageRow { Status = status }.StatusLabel.Should().Be(expected);
    }

    [Fact]
    public void StatusPill_EachStatusHasItsOwnColours()
    {
        var queued = new MessageRow { Status = "queued" };
        var sent = new MessageRow { Status = "sent" };
        var failed = new MessageRow { Status = "failed" };
        var unknown = new MessageRow { Status = "something-new" };

        new[] { queued.StatusBg, sent.StatusBg, failed.StatusBg }.Should().OnlyHaveUniqueItems(
            "the pill colour is what distinguishes the folders at a glance in the merged view");
        new[] { queued.StatusFg, sent.StatusFg, failed.StatusFg }.Should().OnlyHaveUniqueItems();
        unknown.StatusBg.Should().Be(new MessageRow().StatusBg,
            "an unknown status must fall back to the neutral pill, never crash the grid");
    }

    [Fact]
    public void ReadFolders_CapsTheMergedResult_NotEachFolder()
    {
        var t0 = new DateTime(2026, 6, 11, 0, 0, 0, DateTimeKind.Utc);
        var queue = SubDir("queue");
        var sent = SubDir("sent");

        // A busy queue plus one much newer sent message: the newest entry must survive
        for (int i = 0; i < MailFolderReader.MaxEntries; i++)
            WriteMetaTo(queue, $"q{i:D4}", new MailMetadata { MessageId = $"q{i:D4}", Subject = $"q{i:D4}", ReceivedAt = t0.AddSeconds(i) });
        WriteMetaTo(sent, "newest", new MailMetadata { MessageId = "newest", Subject = "newest", ReceivedAt = t0.AddDays(1) });

        var rows = ReadMerged(queue, sent);

        rows.Should().HaveCount(MailFolderReader.MaxEntries);
        rows[0].Subject.Should().Be("newest",
            "capping per folder before merging would drop the newest message of a quiet folder");
    }

    // ── Paging ───────────────────────────────────────────────────────────────

    private void WriteMessages(int count, string prefix = "m")
    {
        var t0 = new DateTime(2026, 6, 11, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < count; i++)
            WriteMeta($"{prefix}{i:D4}", new MailMetadata
            {
                MessageId = $"{prefix}{i:D4}",
                Subject = $"s{i:D4}",
                ReceivedAt = t0.AddSeconds(i),
            });
    }

    [Fact]
    public void ReadFolder_MoreThanOnePage_ReportsHasMoreAndTheFolderTotal()
    {
        WriteMessages(12);

        var result = MailFolderReader.ReadFolder(_dir, limit: 5, search: null);

        result.Rows.Should().HaveCount(5);
        result.Total.Should().Be(12, "the counter names what the page was drawn from");
        result.HasMore.Should().BeTrue();
    }

    [Fact]
    public void ReadFolder_WholeFolderFitsInTheLimit_ReportsNoMore()
    {
        WriteMessages(3);

        MailFolderReader.ReadFolder(_dir, limit: 500, search: null).HasMore.Should().BeFalse(
            "offering another page when the folder is exhausted misleads");
    }

    [Fact]
    public void ReadFolder_RaisedLimit_ReachesFurtherBack()
    {
        WriteMessages(12);

        var page2 = MailFolderReader.ReadFolder(_dir, limit: 10, search: null).Rows;

        page2.Should().HaveCount(10);
        page2[^1].Subject.Should().Be("s0002", "\"load more\" must reach older messages, not reshuffle");
    }

    // ── Search ───────────────────────────────────────────────────────────────

    [Fact]
    public void ReadFolder_Search_LooksBeyondTheFirstPage()
    {
        WriteMessages(50);
        WriteMeta("needle", new MailMetadata
        {
            MessageId = "needle",
            Subject = "the-needle",
            ReceivedAt = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc),   // oldest of all
        });

        var result = MailFolderReader.ReadFolder(_dir, limit: 5, search: "needle");

        result.Rows.Should().ContainSingle(r => r.Subject == "the-needle",
            "a search restricted to the loaded page would miss exactly the old message being looked for");
        result.Total.Should().Be(51, "the total stays the folder size, not the match count");
    }

    [Fact]
    public void ReadFolder_SearchMatchingNothing_ReturnsEmptyWithoutHasMore()
    {
        WriteMessages(10);

        var result = MailFolderReader.ReadFolder(_dir, limit: 5, search: "fabrikam");

        result.Rows.Should().BeEmpty();
        result.HasMore.Should().BeFalse();
    }

    [Fact]
    public void ReadFolders_Search_AppliesAcrossTheMergedFolders()
    {
        var queue = SubDir("queue");
        var sent = SubDir("sent");
        WriteMetaTo(queue, "q1", new MailMetadata { MessageId = "q1", From = "app@contoso.com", ReceivedAt = DateTime.UtcNow });
        WriteMetaTo(sent, "s1", new MailMetadata { MessageId = "s1", From = "other@fabrikam.com", ReceivedAt = DateTime.UtcNow });

        var result = MailFolderReader.ReadFolders(MailFolderReader.PageSize, "contoso", queue, sent);

        result.Rows.Should().ContainSingle().Which.From.Should().Be("app@contoso.com");
    }

    [Theory]
    [InlineData("contoso")]              // sender
    [InlineData("ops@")]                 // recipient
    [InlineData("backup")]               // subject
    [InlineData("failed")]               // status label
    [InlineData("10.0.0.5")]             // client IP
    [InlineData("mailboxnotenabled")]    // failure reason, lower-cased
    public void Matches_TermInAnySearchedField_ReturnsTrue(string term)
    {
        var row = new MessageRow
        {
            From = "app@contoso.com",
            To = "ops@contoso.com",
            Subject = "Nightly Backup Report",
            Status = "failed",
            ClientIp = "10.0.0.5",
            LastError = "MailboxNotEnabledForRESTAPI",
        };

        row.Matches(term).Should().BeTrue();
    }

    [Fact]
    public void Matches_EmptySearch_MatchesEveryRow()
    {
        new MessageRow().Matches("   ").Should().BeTrue("a whitespace-only box is an empty box");
    }

    [Fact]
    public void Matches_TermInNoField_ReturnsFalse()
    {
        new MessageRow { From = "app@contoso.com" }.Matches("fabrikam").Should().BeFalse();
    }
}
