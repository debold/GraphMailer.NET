using GraphMailer.ConfigTool.Helpers;

namespace GraphMailer.Tests.Unit.ConfigTool;

/// <summary>
/// The line under the Messages list: an empty list must say why, instead of looking like
/// "no mail was handled".
/// </summary>
public sealed class MessageListHintTests
{
    private static string? Hint(int rows = 0, bool searching = false, MessageListFilter filter = MessageListFilter.All,
        bool archiveSent = false, bool metricsEnabled = true, bool dbExists = true, string? error = null)
        => MessageListHint.Build(rows, searching, filter, archiveSent, metricsEnabled, dbExists, error);

    [Fact]
    public void Read_error_is_shown_even_when_rows_are_listed()
        => Hint(rows: 12, error: "database is locked").Should().Contain("could not be read").And.Contain("database is locked");

    [Fact]
    public void Switched_off_metrics_explain_the_empty_list()
        => Hint(metricsEnabled: false).Should().Contain("Metrics are switched off").And.Contain("not archived");

    [Fact]
    public void Switched_off_metrics_with_archive_do_not_mention_the_archive()
        => Hint(metricsEnabled: false, archiveSent: true).Should().NotContain("archived");

    [Fact]
    public void Missing_database_says_nothing_was_recorded_yet()
        => Hint(dbExists: false).Should().Contain("No message history recorded yet");

    [Fact]
    public void Empty_list_with_recording_on_says_so()
        => Hint().Should().Contain("No messages recorded yet");

    [Fact]
    public void Listed_rows_or_a_search_need_no_hint()
    {
        Hint(rows: 3).Should().BeNull();
        Hint(searching: true).Should().BeNull("an empty search result explains itself");
        Hint(filter: MessageListFilter.Failed).Should().BeNull();
    }
}
