using FluentAssertions;
using GraphMailer.ConfigTool.Helpers;
using GraphMailer.ConfigTool.Services;
using GraphMailer.Service.Infrastructure.Metrics;

namespace GraphMailer.Tests.Unit.ConfigTool;

/// <summary>
/// The Messages page's list merge, timeline and summary sentence. The summary is what an operator
/// copies into a reply ("was my mail delivered?"), so its wording is pinned down here.
/// </summary>
public sealed class MessageTimelineTests
{
    private static readonly DateTime Received = new(2026, 11, 12, 10, 14, 2);

    private static MessageRow Row(string id, string status, int failed = 0, DateTime? sent = null, bool traced = true,
        DateTime? received = null, string lastError = "") => new()
    {
        MessageId = id,
        ReceivedAt = received ?? Received,
        From = "scanner@contoso.de",
        To = "buchhaltung@contoso.de",
        Subject = "Scan",
        Status = status,
        FailedAttempts = failed,
        Attempts = (status == "sent" ? failed + 1 : failed).ToString(),
        SentAt = sent,
        HasTrace = traced,
        LastError = lastError,
    };

    private static TraceEventRow Ev(int minute, string stage, string outcome, string summary, string? detail = null)
        => new(Received.AddMinutes(minute), stage, outcome, summary, detail, null);

    // ── Merge ────────────────────────────────────────────────────────────────

    [Fact]
    public void Merge_keeps_history_rows_and_adds_folder_only_messages_as_legacy()
    {
        var traced = new[] { Row("both", "queued", 1) };
        var folders = new[]
        {
            Row("both", "queued", 2, traced: false) ,
            Row("legacy", "sent", 0, sent: Received.AddMinutes(1), traced: false, received: Received.AddMinutes(-5)),
        };

        var rows = MessageTimeline.Merge(traced, 1, folders, MessageListFilter.All, null, 50, out var total, out var hasMore);

        rows.Select(r => r.MessageId).Should().Equal("both", "legacy");
        rows[0].HasTrace.Should().BeTrue();
        rows[0].FailedAttempts.Should().Be(2, "the queue folder is the live truth while a message waits");
        rows[1].HasTrace.Should().BeFalse();
        total.Should().Be(2);
        hasMore.Should().BeFalse();
    }

    [Fact]
    public void Merge_applies_the_filter_to_folder_only_messages()
    {
        var folders = new[]
        {
            Row("late", "sent", 2, sent: Received.AddHours(1), traced: false),
            Row("ontime", "sent", 0, sent: Received.AddSeconds(3), traced: false),
            Row("waiting", "queued", 0, traced: false),
        };

        var rows = MessageTimeline.Merge([], 0, folders, MessageListFilter.Delayed, null, 50, out _, out _);

        rows.Select(r => r.MessageId).Should().Equal("late");
    }

    [Fact]
    public void Merge_pages_the_combined_list_newest_first()
    {
        var traced = Enumerable.Range(0, 3).Select(i => Row($"t{i}", "sent", received: Received.AddMinutes(i * 2))).ToArray();
        var folders = Enumerable.Range(0, 3).Select(i => Row($"f{i}", "sent", traced: false, received: Received.AddMinutes(i * 2 + 1))).ToArray();

        var rows = MessageTimeline.Merge(traced, 3, folders, MessageListFilter.All, null, 4, out var total, out var hasMore);

        rows.Select(r => r.MessageId).Should().Equal("f2", "t2", "f1", "t1");
        total.Should().Be(6);
        hasMore.Should().BeTrue();
    }

    // ── Timeline ─────────────────────────────────────────────────────────────

    [Fact]
    public void Build_groups_steps_under_section_headings()
    {
        var events = new[]
        {
            Ev(0, TraceStages.Smtp, TraceOutcomes.Ok, "Received"),
            Ev(0, TraceStages.Smtp, TraceOutcomes.Ok, "Accepted"),
            Ev(0, TraceStages.Scan, TraceOutcomes.Ok, "Clean"),
            Ev(0, TraceStages.Rules, TraceOutcomes.Ok, "Rule"),
            Ev(1, TraceStages.Graph, TraceOutcomes.Ok, "Delivered"),
        };

        var items = MessageTimeline.Build(Row("m", "sent"), events, null);

        items.Select(i => i.Group).Should().Equal("SMTP", null, "Processing", null, "Delivery");
    }

    [Fact]
    public void Build_shortens_a_long_run_of_failed_attempts()
    {
        var events = new List<TraceEventRow> { Ev(0, TraceStages.Smtp, TraceOutcomes.Ok, "Received") };
        for (var i = 1; i <= 10; i++)
            events.Add(Ev(i, TraceStages.Graph, TraceOutcomes.Retry, $"Attempt {i} failed"));
        events.Add(Ev(20, TraceStages.Graph, TraceOutcomes.Ok, "Delivered"));

        var items = MessageTimeline.Build(Row("m", "sent", 10), events, null);

        items.Select(i => i.Summary).Should().Equal(
            "Received", "Attempt 1 failed", "Attempt 2 failed", "6 more failed attempts",
            "Attempt 9 failed", "Attempt 10 failed", "Delivered");
    }

    [Fact]
    public void Build_keeps_a_short_run_of_failed_attempts()
    {
        var events = Enumerable.Range(1, MessageTimeline.CollapseThreshold)
            .Select(i => Ev(i, TraceStages.Graph, TraceOutcomes.Retry, $"Attempt {i} failed")).ToList();

        MessageTimeline.Build(Row("m", "queued", 5), events, null).Should().HaveCount(MessageTimeline.CollapseThreshold);
    }

    [Fact]
    public void Build_without_history_pieces_together_receipt_attempts_and_result()
    {
        var items = MessageTimeline.Build(
            new MessageRow
            {
                MessageId = "legacy", ReceivedAt = Received, Status = "sent", FailedAttempts = 3,
                SentAt = Received.AddMinutes(36), ClientIp = "10.0.4.31", LastError = "504 GatewayTimeout",
                LastAttemptAt = Received.AddMinutes(6),
            },
            [], new ReceiptFacts(587, true, "scanner", 2_400_000));

        items.Select(i => i.Summary).Should().Equal(
            "Received from 10.0.4.31 and queued",
            "3 delivery attempts failed, the last one at 10:20",
            "Delivered to Exchange Online");
        items[0].Detail.Should().Be($"port 587 · TLS · signed in as scanner · {2.3:0.#} MB");
        items[1].Detail.Should().Be("504 GatewayTimeout");
    }

    // ── Summary ──────────────────────────────────────────────────────────────

    [Fact]
    public void Summary_of_a_late_delivery_names_the_delay_and_the_last_error()
    {
        var events = new[]
        {
            Ev(0, TraceStages.Graph, TraceOutcomes.Retry, "Attempt 1 failed", "503 ServiceUnavailable · next attempt in 1m 0s"),
            Ev(6, TraceStages.Graph, TraceOutcomes.Retry, "Attempt 2 failed", "504 GatewayTimeout · next attempt in 30m 0s"),
        };
        var row = Row("m", "sent", 2, sent: Received.AddMinutes(36).AddSeconds(7));

        var text = MessageTimeline.Summarize(row, events);

        text.Should().Be($"Delivered on {row.SentAt:d} at 10:50, 36 minutes after it arrived. " +
                         "2 attempts failed before that, the last one with: 504 GatewayTimeout.");
    }

    [Fact]
    public void Summary_of_an_on_time_delivery_is_one_sentence()
        => MessageTimeline.Summarize(Row("m", "sent", sent: Received.AddSeconds(3)), [])
            .Should().Be($"Delivered on {Received:d} at 10:14, 3 seconds after it arrived.");

    [Fact]
    public void Summary_of_a_waiting_message_names_the_next_attempt()
    {
        var row = new MessageRow
        {
            MessageId = "m", ReceivedAt = Received, Status = "queued", FailedAttempts = 1,
            LastError = "503 ServiceUnavailable", NextRetryAt = Received.AddMinutes(5),
        };

        MessageTimeline.Summarize(row, []).Should().Be(
            "Not delivered yet. 1 attempt(s) failed so far, the last one with: 503 ServiceUnavailable. Next attempt at 10:19.");
    }

    [Fact]
    public void Summary_of_a_rejected_message_says_why_and_that_the_sender_was_told()
    {
        var events = new[] { Ev(0, TraceStages.Scan, TraceOutcomes.Failed, "Rejected: malware found in 'invoice.zip'") };

        MessageTimeline.Summarize(Row("m", "rejected"), events).Should().Be(
            "Not accepted. Rejected: malware found in 'invoice.zip'; the sending device got an error reply.");
    }

    [Fact]
    public void Summary_of_a_discarded_message_warns_that_the_sender_believes_it_was_sent()
        => MessageTimeline.Summarize(Row("m", "discarded"), []).Should().EndWith("the sending device was told it was accepted.");

    [Fact]
    public void Copy_text_carries_the_facts_an_answer_needs()
    {
        var row = Row("abc", "sent", sent: Received.AddSeconds(3));

        var text = MessageTimeline.BuildCopyText(row, "Delivered.");

        text.Should().StartWith("Delivered.").And.Contain("Subject:  Scan").And.Contain("From:     scanner@contoso.de")
            .And.Contain("Delivered: 2026-11-12 10:14:05").And.Contain("GraphMailer id: abc");
    }

    [Theory]
    [InlineData(12, "12 seconds")]
    [InlineData(60, "1 minute")]
    [InlineData(36 * 60 + 7, "36 minutes")]
    [InlineData(3600, "1 hour")]
    [InlineData(2 * 3600 + 5 * 60, "2 hours 5 min")]
    public void Delay_reads_naturally(int seconds, string expected)
        => MessageTimeline.FormatDelay(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
}
