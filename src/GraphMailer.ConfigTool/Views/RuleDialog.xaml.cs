using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using GraphMailer.ConfigTool.Helpers;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Config;

namespace GraphMailer.ConfigTool.Views;

/// <summary>
/// Adds or edits one rule: its identity and mode, its conditions, and its actions.
///
/// Action order is editable because it matters — actions run top to bottom, and Reject or
/// Discard end the rule. Condition order does not matter, so those rows have no move buttons.
/// </summary>
public partial class RuleDialog : Window
{
    private readonly ObservableCollection<ConditionRow> _conditions = [];
    private readonly ObservableCollection<ActionRow> _actions = [];
    private readonly Func<string, bool> _isDuplicateName;
    private readonly int _regexTimeoutMs;
    private bool _loading = true;

    /// <summary>The edited rule, valid only when the dialog returned true.</summary>
    internal ConfigDocument.MessageRuleEntry Result { get; private set; } = new();

    /// <param name="isDuplicateName">
    /// Checked against the other rules in the set, so two rules cannot share a name — log lines
    /// and the statistics identify a rule by its name and would otherwise be ambiguous.
    /// </param>
    internal RuleDialog(
        ConfigDocument.MessageRuleEntry? existing = null,
        Func<string, bool>? isDuplicateName = null,
        int regexTimeoutMs = 100)
    {
        InitializeComponent();
        _isDuplicateName = isDuplicateName ?? (_ => false);
        _regexTimeoutMs = regexTimeoutMs;
        Title = existing is null ? "Add rule" : "Edit rule";

        ConditionsGrid.ItemsSource = _conditions;
        ActionsGrid.ItemsSource = _actions;

        var current = existing ?? new ConfigDocument.MessageRuleEntry();
        EnabledBox.IsChecked = current.Enabled;
        NameBox.Text = current.Name;
        DescriptionBox.Text = current.Description ?? string.Empty;
        StopProcessingBox.IsChecked = current.StopProcessing;
        SelectByTag(ModeBox, current.Mode);
        SelectByTag(MatchBox, current.Match);

        foreach (var condition in current.Conditions)
            _conditions.Add(new ConditionRow(Clone(condition)));
        foreach (var action in current.Actions)
            _actions.Add(new ActionRow(Clone(action)));

        _loading = false;
        UpdateEnforceNotice();
        Validate();
    }

    // Rows wrap the entry so the grid can show a rendered summary while the underlying entry
    // stays the thing that is saved.
    private sealed class ConditionRow(ConfigDocument.RuleConditionEntry entry)
    {
        public ConfigDocument.RuleConditionEntry Entry { get; } = entry;
        public string Summary => MessageRuleValidation.Describe(Entry);
    }

    private sealed class ActionRow(ConfigDocument.RuleActionEntry entry)
    {
        public ConfigDocument.RuleActionEntry Entry { get; } = entry;
        public string Summary => MessageRuleValidation.Describe(Entry);

        /// <summary>Marks an action that works but may not have the effect it sounds like.</summary>
        public string WarningGlyph =>
            MessageRuleValidation.DescribeActionWarning(
                MessageRuleModel.Parse(Entry.Type, RuleActionType.PrefixSubject), Entry) is null
                ? string.Empty
                : "⚠";
    }

    private static ConfigDocument.RuleConditionEntry Clone(ConfigDocument.RuleConditionEntry e) => new()
    {
        Field = e.Field,
        Operator = e.Operator,
        Value = e.Value,
        HeaderName = e.HeaderName,
        Negate = e.Negate,
        CaseSensitive = e.CaseSensitive,
    };

    private static ConfigDocument.RuleActionEntry Clone(ConfigDocument.RuleActionEntry e) => new()
    {
        Type = e.Type,
        Value = e.Value,
        Html = e.Html,
        HeaderName = e.HeaderName,
        Recipient = e.Recipient,
        Match = e.Match,
        AttachmentMatch = e.AttachmentMatch,
        SmtpCode = e.SmtpCode,
    };

    private static void SelectByTag(ComboBox box, string value)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if ((item.Tag as string ?? string.Empty).Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    private static string SelectedTag(ComboBox box)
        => box.SelectedItem is ComboBoxItem item ? item.Tag as string ?? string.Empty : string.Empty;

    // ---------------------------------------------------------------- conditions

    private void AddCondition_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RuleConditionDialog(regexTimeoutMs: _regexTimeoutMs) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _conditions.Add(new ConditionRow(dialog.Result));
        Validate();
    }

    private void EditCondition_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ConditionRow row) return;

        var dialog = new RuleConditionDialog(row.Entry, _regexTimeoutMs) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        // The row's summary is computed from the entry, so replacing the row is the way to
        // refresh it — the entry itself is not observable.
        _conditions[_conditions.IndexOf(row)] = new ConditionRow(dialog.Result);
        Validate();
    }

    private void RemoveCondition_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ConditionRow row) return;

        _conditions.Remove(row);
        Validate();
    }

    // ---------------------------------------------------------------- actions

    private void AddAction_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RuleActionDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _actions.Add(new ActionRow(dialog.Result));
        Validate();
    }

    private void EditAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ActionRow row) return;

        var dialog = new RuleActionDialog(row.Entry) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _actions[_actions.IndexOf(row)] = new ActionRow(dialog.Result);
        Validate();
    }

    private void RemoveAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ActionRow row) return;

        _actions.Remove(row);
        Validate();
    }

    private void MoveActionUp_Click(object sender, RoutedEventArgs e) => MoveAction(sender, up: true);

    private void MoveActionDown_Click(object sender, RoutedEventArgs e) => MoveAction(sender, up: false);

    private void MoveAction(object sender, bool up)
    {
        if ((sender as FrameworkElement)?.DataContext is not ActionRow row) return;

        var moved = up ? ListReorder.MoveUp(_actions, row) : ListReorder.MoveDown(_actions, row);
        if (!moved) return;

        ActionsGrid.SelectedItem = row;
        ActionsGrid.ScrollIntoView(row);
    }

    // ---------------------------------------------------------------- validation

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        UpdateEnforceNotice();
        Validate();
    }

    private void Match_Changed(object sender, SelectionChangedEventArgs e) => Validate();

    private void UpdateEnforceNotice()
        => EnforceNotice.Visibility = SelectedTag(ModeBox) == nameof(MessageRuleMode.Enforce)
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void Validate(object? sender = null, RoutedEventArgs? e = null)
    {
        if (_loading) return;

        var name = NameBox.Text.Trim();

        if (name.Length == 0)
        {
            SetError("Give the rule a name — the log and the statistics identify it by name.");
            return;
        }

        if (_isDuplicateName(name))
        {
            SetError("Another rule already uses this name.");
            return;
        }

        if (_actions.Count == 0)
        {
            SetError("Add at least one action — a rule without actions does nothing.");
            return;
        }

        SetError(null);
    }

    private void SetError(string? message)
    {
        ErrorText.Text = message ?? string.Empty;
        ErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = message is null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = new ConfigDocument.MessageRuleEntry
        {
            Enabled = EnabledBox.IsChecked == true,
            Name = NameBox.Text.Trim(),
            Description = DescriptionBox.Text.Trim().Length > 0 ? DescriptionBox.Text.Trim() : null,
            Mode = SelectedTag(ModeBox),
            Match = SelectedTag(MatchBox),
            StopProcessing = StopProcessingBox.IsChecked == true,
            Conditions = [.. _conditions.Select(r => r.Entry)],
            Actions = [.. _actions.Select(r => r.Entry)],
        };

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
