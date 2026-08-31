using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using GraphMailer.ConfigTool.Helpers;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Config;
using GraphMailer.Service.Infrastructure.Rules;
using GraphMailer.Service.Services;
using Microsoft.Win32;
using MimeKit;

namespace GraphMailer.ConfigTool.Views;

/// <summary>
/// The Message Rules page: the master switch, the ordered rule list, and the limits.
///
/// The tester lives in its own window (<see cref="RuleTesterWindow"/>) — its output is long, and
/// this page is already tall enough.
/// </summary>
public partial class MessageRulesPage : UserControl
{
    // Kept in sync with the h:NumericField bounds in the XAML; used for the save-blocking check,
    // which must hold even for a page the user never opened (and which therefore has no visual
    // tree for NumericField.SubtreeHasErrors to walk).
    private const int BodyKbMin = 1, BodyKbMax = 102_400;
    private const int RegexTimeoutMin = 1, RegexTimeoutMax = 10_000;
    private const int RetentionMin = 1, RetentionMax = 3650;

    private readonly Action _markDirty;
    private readonly Func<string?> _dataDir;
    private readonly Func<IReadOnlyList<int>> _configuredPorts;
    private readonly ObservableCollection<RuleRow> _rules = [];
    private IReadOnlyDictionary<string, int> _hits = new Dictionary<string, int>();
    private bool _loading;

    /// <param name="dataDir">Where metrics.db lives, for the per-rule hit counts.</param>
    /// <param name="configuredPorts">
    /// Live listener ports, so the tester offers a port a message could actually arrive on
    /// rather than a free-text number. A callback rather than a snapshot, so a listener added
    /// on the Servers page is selectable here without saving in between.
    /// </param>
    public MessageRulesPage(Action markDirty, Func<string?> dataDir, Func<IReadOnlyList<int>> configuredPorts)
    {
        _markDirty = markDirty;
        _dataDir = dataDir;
        _configuredPorts = configuredPorts;
        InitializeComponent();

        RulesGrid.ItemsSource = _rules;

        // Refresh on every visit: the service records hits while the tool is open, so a rule
        // that started firing since the last look should say so where it is edited.
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) LoadHits();
        };
    }

    /// <summary>One rule in the grid. The entry is the thing that gets saved.</summary>
    private sealed class RuleRow(ConfigDocument.MessageRuleEntry entry, Func<RuleRow, int> position, Func<string, int> hits)
        : INotifyPropertyChanged
    {
        public ConfigDocument.MessageRuleEntry Entry { get; } = entry;

        public int Position => position(this);

        public bool Enabled
        {
            get => Entry.Enabled;
            set
            {
                if (Entry.Enabled == value) return;
                Entry.Enabled = value;
                OnPropertyChanged();
            }
        }

        public string Name => Entry.Name;
        public string Mode => Entry.Mode;

        public string MatchSummary => Entry.Conditions.Count == 0
            ? "every message"
            : Entry.Match.Equals(nameof(ConditionMatch.Any), StringComparison.OrdinalIgnoreCase)
                ? $"any of {Entry.Conditions.Count}"
                : $"all of {Entry.Conditions.Count}";

        public string ActionSummary =>
            string.Join(" · ", Entry.Actions.Select(MessageRuleValidation.Describe));

        /// <summary>True when the rule carries an action that may not have the effect it sounds like.</summary>
        private bool HasActionWarning => Entry.Actions.Any(a =>
            MessageRuleValidation.DescribeActionWarning(
                MessageRuleModel.Parse(a.Type, RuleActionType.PrefixSubject), a) is not null);

        /// <summary>
        /// Both rare markers in one named column. Two separate anonymous columns that are empty
        /// on almost every row read as a broken grid rather than as information.
        /// </summary>
        public string NotesGlyphs =>
            (HasActionWarning ? "⚠" : string.Empty) + (Entry.StopProcessing ? "■" : string.Empty);

        public string? NotesTooltip
        {
            get
            {
                var parts = new List<string>();
                if (HasActionWarning) parts.Add("⚠ An action may not have the effect it sounds like — open the rule to see why.");
                if (Entry.StopProcessing) parts.Add("■ No rule below this one runs for a matching message.");
                return parts.Count == 0 ? null : string.Join("\n", parts);
            }
        }

        public int Hits => hits(Entry.Name);

        /// <summary>Blank rather than "0" while no statistics exist at all, so an empty column does not read as "never fired".</summary>
        public string HitsDisplay => Hits > 0 ? Hits.ToString() : "—";

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Refresh()
        {
            OnPropertyChanged(string.Empty);
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? string.Empty));
    }

    /// <summary>
    /// True when the page holds a value that must not reach the configuration file. Checked by
    /// the main window before saving, in addition to the numeric-field highlighting.
    /// </summary>
    internal bool HasValidationErrors =>
        !IsInRange(MaxBodyScanKb.Text, BodyKbMin, BodyKbMax)
        || !IsInRange(RegexTimeoutMs.Text, RegexTimeoutMin, RegexTimeoutMax)
        || !IsInRange(DiscardRetentionDays.Text, RetentionMin, RetentionMax);

    private static bool IsInRange(string text, int min, int max)
        => int.TryParse(text?.Trim(), out var value) && value >= min && value <= max;

    private static int ParseBounded(string text, int min, int max, int fallback)
        => int.TryParse(text?.Trim(), out var value) && value >= min && value <= max ? value : fallback;

    private int CurrentRegexTimeout => ParseBounded(RegexTimeoutMs.Text, RegexTimeoutMin, RegexTimeoutMax, 100);

    // ---------------------------------------------------------------- load / collect

    internal void LoadFrom(ConfigDocument doc)
    {
        _loading = true;
        try
        {
            var section = doc.MessageRules;

            EnabledBox.IsChecked = section.Enabled;
            StoreDiscardedBox.IsChecked = section.StoreDiscardedMessages;
            MaxBodyScanKb.Text = Math.Max(1, section.MaxBodyScanBytes / 1024).ToString();
            RegexTimeoutMs.Text = section.RegexTimeoutMs.ToString();
            DiscardRetentionDays.Text = section.DiscardRecordRetentionDays.ToString();

            _rules.Clear();
            foreach (var rule in section.Rules)
                _rules.Add(NewRow(rule));
        }
        finally
        {
            _loading = false;
        }

        LoadHits();
        RefreshSummary();
    }

    internal void CollectTo(ConfigDocument doc)
    {
        doc.MessageRules.Enabled = EnabledBox.IsChecked == true;
        doc.MessageRules.StoreDiscardedMessages = StoreDiscardedBox.IsChecked == true;
        doc.MessageRules.MaxBodyScanBytes =
            (long)ParseBounded(MaxBodyScanKb.Text, BodyKbMin, BodyKbMax, 1024) * 1024;
        doc.MessageRules.RegexTimeoutMs = ParseBounded(RegexTimeoutMs.Text, RegexTimeoutMin, RegexTimeoutMax, 100);
        doc.MessageRules.DiscardRecordRetentionDays =
            ParseBounded(DiscardRetentionDays.Text, RetentionMin, RetentionMax, 60);

        // Grid order is rule order — that is the whole point of the move buttons.
        doc.MessageRules.Rules = [.. _rules.Select(r => r.Entry)];
    }

    /// <summary>The section as it stands on screen, without going through a save.</summary>
    private ConfigDocument.MessageRulesSection CurrentSection()
    {
        var doc = new ConfigDocument();
        CollectTo(doc);
        return doc.MessageRules;
    }

    private RuleRow NewRow(ConfigDocument.MessageRuleEntry entry)
        => new(entry, r => _rules.IndexOf(r) + 1, HitsFor);

    private int HitsFor(string ruleName)
        => _hits.TryGetValue(ruleName, out var count) ? count : 0;

    // ---------------------------------------------------------------- summary

    private void RefreshSummary()
    {
        if (_loading) return;

        var enabled = _rules.Count(r => r.Entry.Enabled);
        var enforcing = _rules.Count(r =>
            r.Entry.Enabled && r.Entry.Mode.Equals(nameof(MessageRuleMode.Enforce), StringComparison.OrdinalIgnoreCase));

        RuleCountLabel.Text = _rules.Count == 0
            ? "No rules configured."
            : $"{_rules.Count} rule(s), {enabled} active, {enforcing} in Enforce mode.";

        var active = EnabledBox.IsChecked == true;
        EnforceNotice.Visibility = active && enforcing > 0 ? Visibility.Visible : Visibility.Collapsed;
        EnforceNoticeText.Text =
            $"{enforcing} rule(s) are in Enforce mode and change or refuse live mail. "
            + "Audit mode records what a rule would do without touching the message.";

        var problems = MessageRuleValidation.FindProblems(CurrentSection());
        ProblemNotice.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ProblemText.Text = string.Join(Environment.NewLine,
            problems.Select(p => $"• {p.RuleName}: {p.Detail}"));

        foreach (var row in _rules)
            row.Refresh();
    }

    private void LoadHits()
    {
        var dir = _dataDir();
        _hits = string.IsNullOrWhiteSpace(dir)
            ? new Dictionary<string, int>()
            : MessageRuleStatsReader.ReadHitTotals(Path.Combine(dir, "metrics.db"));

        foreach (var row in _rules)
            row.Refresh();
    }

    // ---------------------------------------------------------------- rule commands

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RuleDialog(
            isDuplicateName: name => MessageRuleValidation.IsDuplicateName(_rules.Select(r => r.Entry), name),
            regexTimeoutMs: CurrentRegexTimeout)
        { Owner = Window.GetWindow(this) };

        if (dialog.ShowDialog() != true) return;

        _rules.Add(NewRow(dialog.Result));
        Changed();
    }

    private void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RuleRow row) return;

        var dialog = new RuleDialog(
            row.Entry,
            name => MessageRuleValidation.IsDuplicateName(_rules.Select(r => r.Entry), name, row.Entry),
            CurrentRegexTimeout)
        { Owner = Window.GetWindow(this) };

        if (dialog.ShowDialog() != true) return;

        // The row's columns are computed from the entry, so the row is replaced rather than
        // mutated — the entry itself is not observable.
        _rules[_rules.IndexOf(row)] = NewRow(dialog.Result);
        Changed();
    }

    private void DuplicateRule_Click(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not RuleRow row)
        {
            MessageBox.Show(Window.GetWindow(this), "Select the rule to duplicate first.",
                "Duplicate rule", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var copy = MessageRuleModel.ToRule(row.Entry);
        var entry = new ConfigDocument.MessageRuleEntry
        {
            // A copy starts disabled and in Audit mode: duplicating a rule is how a variant gets
            // written, and the variant should not act on live mail before it has been edited.
            Enabled = false,
            Mode = nameof(MessageRuleMode.Audit),
            Name = UniqueName(row.Entry.Name),
            Description = row.Entry.Description,
            Match = row.Entry.Match,
            StopProcessing = row.Entry.StopProcessing,
            Conditions = [.. copy.Conditions.Select(c => new ConfigDocument.RuleConditionEntry
            {
                Field = c.Field.ToString(),
                Operator = c.Operator.ToString(),
                Value = c.Value,
                HeaderName = c.HeaderName,
                Negate = c.Negate,
                CaseSensitive = c.CaseSensitive,
            })],
            Actions = [.. copy.Actions.Select(a => new ConfigDocument.RuleActionEntry
            {
                Type = a.Type.ToString(),
                Value = a.Value,
                Html = a.Html,
                HeaderName = a.HeaderName,
                Recipient = a.Recipient?.ToString(),
                Match = a.Match,
                AttachmentMatch = a.AttachmentMatch?.ToString(),
                SmtpCode = a.SmtpCode,
            })],
        };

        _rules.Insert(_rules.IndexOf(row) + 1, NewRow(entry));
        Changed();
    }

    private string UniqueName(string baseName)
    {
        var candidate = $"{baseName} (copy)";
        var n = 2;
        while (MessageRuleValidation.IsDuplicateName(_rules.Select(r => r.Entry), candidate))
            candidate = $"{baseName} (copy {n++})";
        return candidate;
    }

    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RuleRow row) return;

        _rules.Remove(row);
        Changed();
    }

    private void MoveRuleUp_Click(object sender, RoutedEventArgs e) => MoveRule(sender, up: true);

    private void MoveRuleDown_Click(object sender, RoutedEventArgs e) => MoveRule(sender, up: false);

    private void MoveRule(object sender, bool up)
    {
        if ((sender as FrameworkElement)?.DataContext is not RuleRow row) return;

        var moved = up ? ListReorder.MoveUp(_rules, row) : ListReorder.MoveDown(_rules, row);
        if (!moved) return;

        RulesGrid.SelectedItem = row;
        RulesGrid.ScrollIntoView(row);
        Changed();
    }

    private void RuleEnabled_Changed(object sender, RoutedEventArgs e) => Changed();

    private void Enabled_Changed(object sender, RoutedEventArgs e) => Changed();

    private void AnyValue_Changed(object sender, RoutedEventArgs e) => Changed();

    private void RefreshHits_Click(object sender, RoutedEventArgs e) => LoadHits();

    private void Changed()
    {
        if (_loading) return;

        RefreshSummary();
        _markDirty();
    }

    // ---------------------------------------------------------------- rule tester

    /// <summary>
    /// Opens the tester in its own window. The rules are handed over as a callback, so it always
    /// runs what is currently on the page — testing a rule you have just edited is the point.
    /// </summary>
    private void OpenTester_Click(object sender, RoutedEventArgs e)
    {
        var tester = new RuleTesterWindow(CurrentSection, _configuredPorts)
        {
            Owner = Window.GetWindow(this),
        };
        tester.ShowDialog();
    }
}
