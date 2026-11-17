using FluentAssertions;
using GraphMailer.ConfigTool.Helpers;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Metrics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GraphMailer.Tests.Unit.Infrastructure.Metrics;

/// <summary>
/// The message history behind the ConfigTool's Messages timeline: what the service writes with
/// <see cref="MetricsService.RecordTraceAsync"/> and what <see cref="MessageTraceReader"/> reads
/// back. Tested as a round trip, because the two halves only agree through the table layout.
/// </summary>
public sealed class MessageTraceTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "messagetrace-tests-" + Guid.NewGuid().ToString("N"));

    public MessageTraceTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* SQLite may still hold the file — ignored in teardown */ }
    }

    private string DbPath => Path.Combine(_tempDir, "data", "metrics.db");

    private MetricsService CreateService(bool enabled = true, int retentionDays = 90)
    {
        var monitor = Substitute.For<IOptionsMonitor<MetricsOptions>>();
        monitor.CurrentValue.Returns(new MetricsOptions { Enabled = enabled, RetentionDays = retentionDays, BasePath = _tempDir });
        return new MetricsService(monitor, NullLogger<MetricsService>.Instance);
    }

    private static MessageTraceHeader Header(string id, DateTime? receivedUtc = null, string subject = "Scan") => new()
    {
        MessageId = id,
        ReceivedAtUtc = receivedUtc ?? DateTime.UtcNow,
        From = "scanner@contoso.de",
        To = ["buchhaltung@contoso.de"],
        Subject = subject,
        ClientIp = "10.0.4.31",
        SizeBytes = 2_400_000,
    };

    private static MessageTraceEvent Step(string stage, string outcome, string summary, int? attempt = null)
        => new(DateTime.UtcNow, stage, outcome, summary, Attempt: attempt);

    [Fact]
    public async Task Receipt_creates_a_list_entry_with_its_steps()
    {
        var svc = CreateService();

        await svc.RecordTraceAsync(Header("m1"), TraceStatuses.Queued, 0,
        [
            Step(TraceStages.Smtp, TraceOutcomes.Ok, "Received from 10.0.4.31 on port 587"),
            Step(TraceStages.Scan, TraceOutcomes.Ok, "Malware scan: clean"),
            Step(TraceStages.Smtp, TraceOutcomes.Ok, "Accepted and queued (250 OK)"),
        ]);

        var (rows, total, hasMore) = MessageTraceReader.ReadList(DbPath, MessageListFilter.All, null, 50);
        rows.Should().ContainSingle();
        total.Should().Be(1);
        hasMore.Should().BeFalse();
        var row = rows[0];
        row.Status.Should().Be("queued");
        row.Subject.Should().Be("Scan");
        row.To.Should().Be("buchhaltung@contoso.de");
        row.HasTrace.Should().BeTrue();

        MessageTraceReader.ReadEvents(DbPath, "m1").Select(e => e.Summary).Should().Equal(
            "Received from 10.0.4.31 on port 587", "Malware scan: clean", "Accepted and queued (250 OK)");
    }

    [Fact]
    public async Task Later_updates_move_status_and_attempts_but_keep_what_was_known()
    {
        var svc = CreateService();
        await svc.RecordTraceAsync(Header("m1", subject: "Original"), TraceStatuses.Queued, 0,
            [Step(TraceStages.Smtp, TraceOutcomes.Ok, "Accepted")]);

        // A delivery update carries less: no subject, no size.
        var sparse = Header("m1", subject: string.Empty) with { SizeBytes = 0 };
        await svc.RecordTraceAsync(sparse, TraceStatuses.Queued, 1,
            [Step(TraceStages.Graph, TraceOutcomes.Retry, "Attempt 1 failed", 1)]);
        await svc.RecordTraceAsync(sparse, TraceStatuses.Sent, 2,
            [Step(TraceStages.Graph, TraceOutcomes.Ok, "Delivered to Exchange Online on attempt 2", 2)]);

        var row = MessageTraceReader.ReadList(DbPath, MessageListFilter.All, null, 50).Rows.Single();
        row.Status.Should().Be("sent");
        row.Subject.Should().Be("Original", "an update without a subject must not erase it");
        row.SizeBytes.Should().Be(2_400_000);
        row.FailedAttempts.Should().Be(1, "two attempts, the second delivered");
        row.IsSentLate.Should().BeTrue();
        row.SentAt.Should().NotBeNull();
        MessageTraceReader.ReadEvents(DbPath, "m1").Should().HaveCount(3);
    }

    [Fact]
    public async Task Status_null_keeps_the_current_status()
    {
        var svc = CreateService();
        await svc.RecordTraceAsync(Header("m1"), TraceStatuses.Queued, 0, []);
        await svc.RecordTraceAsync(Header("m1"), status: null, attempts: 3, []);

        var row = MessageTraceReader.ReadList(DbPath, MessageListFilter.All, null, 50).Rows.Single();
        row.Status.Should().Be("queued");
        row.FailedAttempts.Should().Be(3);
    }

    [Fact]
    public async Task Filters_select_the_matching_messages()
    {
        var svc = CreateService();
        await svc.RecordTraceAsync(Header("queued"), TraceStatuses.Queued, 0, []);
        await svc.RecordTraceAsync(Header("retrying"), TraceStatuses.Queued, 2, []);
        await svc.RecordTraceAsync(Header("sent"), TraceStatuses.Sent, 1, []);
        await svc.RecordTraceAsync(Header("late"), TraceStatuses.Sent, 3, []);
        await svc.RecordTraceAsync(Header("failed"), TraceStatuses.Failed, 9, []);
        await svc.RecordTraceAsync(Header("rejected"), TraceStatuses.Rejected, 0, []);
        await svc.RecordTraceAsync(Header("discarded"), TraceStatuses.Discarded, 0, []);

        Ids(MessageListFilter.Queue).Should().BeEquivalentTo(["queued", "retrying"]);
        Ids(MessageListFilter.Delayed).Should().BeEquivalentTo(["retrying", "late"]);
        Ids(MessageListFilter.Sent).Should().BeEquivalentTo(["sent", "late"]);
        Ids(MessageListFilter.Failed).Should().BeEquivalentTo(["failed"]);
        Ids(MessageListFilter.Rejected).Should().BeEquivalentTo(["rejected", "discarded"]);
        Ids(MessageListFilter.All).Should().HaveCount(7);

        IEnumerable<string> Ids(MessageListFilter f)
            => MessageTraceReader.ReadList(DbPath, f, null, 50).Rows.Select(r => r.MessageId);
    }

    [Fact]
    public async Task Search_finds_messages_by_address_subject_and_by_their_steps()
    {
        var svc = CreateService();
        await svc.RecordTraceAsync(Header("a", subject: "Invoice 4711"), TraceStatuses.Queued, 1,
            [Step(TraceStages.Graph, TraceOutcomes.Retry, "Attempt 1 failed") with { Detail = "503 ServiceUnavailable" }]);
        await svc.RecordTraceAsync(Header("b", subject: "Reminder"), TraceStatuses.Sent, 1, []);

        Search("4711").Should().Equal("a");
        Search("ServiceUnavailable").Should().Equal("a");
        Search("buchhaltung").Should().HaveCount(2);
        Search("100%_nothing").Should().BeEmpty("LIKE wildcards in the term are literal");

        IEnumerable<string> Search(string term)
            => MessageTraceReader.ReadList(DbPath, MessageListFilter.All, term, 50).Rows.Select(r => r.MessageId);
    }

    [Fact]
    public async Task Newest_first_with_paging()
    {
        var svc = CreateService();
        var start = DateTime.UtcNow.AddHours(-1);
        for (var i = 0; i < 5; i++)
            await svc.RecordTraceAsync(Header($"m{i}", start.AddMinutes(i)), TraceStatuses.Sent, 1, []);

        var (rows, total, hasMore) = MessageTraceReader.ReadList(DbPath, MessageListFilter.All, null, 3);
        rows.Select(r => r.MessageId).Should().Equal("m4", "m3", "m2");
        total.Should().Be(5);
        hasMore.Should().BeTrue();
    }

    [Fact]
    public async Task Disabled_metrics_record_nothing()
    {
        var svc = CreateService(enabled: false);
        await svc.RecordTraceAsync(Header("m1"), TraceStatuses.Queued, 0, [Step(TraceStages.Smtp, TraceOutcomes.Ok, "x")]);

        MessageTraceReader.ReadList(DbPath, MessageListFilter.All, null, 50).Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Cleanup_removes_history_not_touched_within_the_retention()
    {
        var svc = CreateService(retentionDays: 30);
        await svc.RecordTraceAsync(Header("old"), TraceStatuses.Sent, 1, [Step(TraceStages.Graph, TraceOutcomes.Ok, "Delivered")]);
        await svc.RecordTraceAsync(Header("new"), TraceStatuses.Sent, 1, [Step(TraceStages.Graph, TraceOutcomes.Ok, "Delivered")]);

        // Age the first message: last touched 40 days ago.
        using (var conn = new SqliteConnection(MetricsDatabase.ConnectionString(DbPath)))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE message_traces SET updated_at = $old WHERE message_id = 'old'; " +
                              "UPDATE message_trace_events SET occurred_at = $old WHERE message_id = 'old';";
            cmd.Parameters.AddWithValue("$old", DateTime.UtcNow.AddDays(-40).ToString("O"));
            cmd.ExecuteNonQuery();
        }

        await svc.CleanupOldRecordsAsync();

        MessageTraceReader.ReadList(DbPath, MessageListFilter.All, null, 50).Rows.Select(r => r.MessageId).Should().Equal("new");
        MessageTraceReader.ReadEvents(DbPath, "old").Should().BeEmpty();
        MessageTraceReader.ReadEvents(DbPath, "new").Should().ContainSingle();
    }

    [Fact]
    public void Reader_returns_nothing_for_a_missing_database()
    {
        var missing = Path.Combine(_tempDir, "nope", "metrics.db");
        MessageTraceReader.ReadList(missing, MessageListFilter.All, null, 50).Rows.Should().BeEmpty();
        MessageTraceReader.ReadEvents(missing, "x").Should().BeEmpty();
        MessageTraceReader.ReadReceipt(missing, "x").Should().BeNull();
    }

    [Fact]
    public async Task Receipt_facts_come_from_the_received_metrics_row()
    {
        var svc = CreateService();
        await svc.RecordEmailReceivedAsync(new ReceivedEmailEvent
        {
            From = "a@b.c",
            To = ["d@e.f"],
            MessageId = "legacy",
            ListenerPort = 587,
            Tls = true,
            Authenticated = true,
            AuthUser = "scanner",
            SizeBytes = 1234,
        });

        MessageTraceReader.ReadReceipt(DbPath, "legacy").Should().Be(new ReceiptFacts(587, true, "scanner", 1234));
    }
}
