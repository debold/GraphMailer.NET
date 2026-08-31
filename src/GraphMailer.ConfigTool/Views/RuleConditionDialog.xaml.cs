using System.Windows;
using System.Windows.Controls;
using GraphMailer.ConfigTool.Helpers;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Config;
using GraphMailer.Service.Infrastructure.Rules;

namespace GraphMailer.ConfigTool.Views;

/// <summary>
/// Adds or edits one condition of a rule.
///
/// The operator list is filtered by the selected field through the service's own schema, so a
/// combination that could never match is not offered at all — a condition that silently never
/// fires is otherwise invisible: the operator sees a configured rule and no effect.
/// </summary>
public partial class RuleConditionDialog : Window
{
    private readonly int _regexTimeoutMs;
    private bool _loading = true;

    /// <summary>The edited condition, valid only when the dialog returned true.</summary>
    internal ConfigDocument.RuleConditionEntry Result { get; private set; } = new();

    internal RuleConditionDialog(ConfigDocument.RuleConditionEntry? existing = null, int regexTimeoutMs = 100)
    {
        InitializeComponent();
        _regexTimeoutMs = regexTimeoutMs;
        Title = existing is null ? "Add condition" : "Edit condition";

        foreach (var field in Enum.GetValues<RuleConditionField>())
            FieldBox.Items.Add(field.ToString());

        var current = existing ?? new ConfigDocument.RuleConditionEntry();
        FieldBox.SelectedItem = current.Field;
        if (FieldBox.SelectedIndex < 0) FieldBox.SelectedIndex = 0;

        PopulateOperators(SelectedField, MessageRuleModel.Parse(current.Operator, RuleConditionOperator.Contains));

        ValueBox.Text = current.Value;
        HeaderNameBox.Text = current.HeaderName ?? string.Empty;
        NegateBox.IsChecked = current.Negate;
        CaseSensitiveBox.IsChecked = current.CaseSensitive;

        _loading = false;
        UpdateFieldVisibility();
        Validate();
    }

    private RuleConditionField SelectedField
        => MessageRuleModel.Parse(FieldBox.SelectedItem as string, RuleConditionField.Subject);

    private RuleConditionOperator SelectedOperator
        => MessageRuleModel.Parse(OperatorBox.SelectedItem as string, RuleConditionOperator.Contains);

    private void PopulateOperators(RuleConditionField field, RuleConditionOperator preferred)
    {
        var operators = MessageRuleValidation.OperatorsFor(field);

        OperatorBox.Items.Clear();
        foreach (var op in operators)
            OperatorBox.Items.Add(op.ToString());

        // Keep the operator when the new field still supports it; otherwise fall back to the
        // first one the field offers rather than leaving an impossible pair selected.
        OperatorBox.SelectedItem = operators.Contains(preferred)
            ? preferred.ToString()
            : operators[0].ToString();
    }

    private void Field_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        PopulateOperators(SelectedField, SelectedOperator);
        UpdateFieldVisibility();
        Validate();
    }

    private void Operator_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        UpdateFieldVisibility();
        Validate();
    }

    private void UpdateFieldVisibility()
    {
        var field = SelectedField;
        var op = SelectedOperator;

        var needsHeader = MessageRuleValidation.NeedsHeaderName(field);
        HeaderNameLabel.Visibility = needsHeader ? Visibility.Visible : Visibility.Collapsed;
        HeaderNameBox.Visibility = needsHeader ? Visibility.Visible : Visibility.Collapsed;

        var needsValue = MessageRuleValidation.NeedsValue(op);
        ValueLabel.Visibility = needsValue ? Visibility.Visible : Visibility.Collapsed;
        ValueBox.Visibility = needsValue ? Visibility.Visible : Visibility.Collapsed;

        // The toggle carries no text of its own, so its label has to follow it.
        var caseSensitivity = needsValue ? Visibility.Visible : Visibility.Collapsed;
        CaseSensitiveBox.Visibility = caseSensitivity;
        CaseSensitiveLabel.Visibility = caseSensitivity;

        HintText.Text = Hint(field, op);
        HintText.Visibility = HintText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// What the operator would otherwise have to discover from behaviour. The multi-value note
    /// is the important one: negation on those fields means "none of them match", which is the
    /// useful reading but not the obvious one.
    /// </summary>
    private static string Hint(RuleConditionField field, RuleConditionOperator op)
    {
        var parts = new List<string>();

        if (RuleConditionSchema.IsMultiValued(field))
            parts.Add("This field can hold several values. The condition is true when any of them "
                      + "matches — inverting it therefore means none of them match.");

        // Said here because the alternative is discovering it from a rule that never fires.
        if (field == RuleConditionField.AttachmentExtension)
            parts.Add("An extension matches with or without the leading dot: \"xml\" and \".xml\" both work.");

        parts.Add(op switch
        {
            RuleConditionOperator.Matches =>
                "Wildcards: * for any text, ? for one character. Separate alternatives with ';'.",
            RuleConditionOperator.RegexMatches =>
                ".NET regular expression. Patterns are run against message content, so they are "
                + "time-limited; a pattern that cannot finish in time counts as no match.",
            RuleConditionOperator.DomainIs =>
                "Matches the exact domain, for example @example.com. Subdomains are not included. "
                + "Separate several domains with ';'.",
            RuleConditionOperator.InIpRange =>
                "An IP address or CIDR range, for example 10.20.0.0/16. Separate several with ';'.",
            RuleConditionOperator.Exists => "True when the field is present and not empty.",
            RuleConditionOperator.IsEmpty => "True when the field is absent or empty.",
            RuleConditionOperator.IsTrue => "True when the condition applies to the message.",
            _ => string.Empty,
        });

        return string.Join(" ", parts.Where(p => p.Length > 0));
    }

    private void Validate(object? sender = null, RoutedEventArgs? e = null)
    {
        if (_loading) return;

        var problem = MessageRuleValidation.ValidateCondition(
            SelectedField,
            SelectedOperator,
            ValueBox.Text,
            HeaderNameBox.Text,
            CaseSensitiveBox.IsChecked == true,
            _regexTimeoutMs);

        SetError(problem);
    }

    private void SetError(string? message)
    {
        ErrorText.Text = message ?? string.Empty;
        ErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = message is null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = new ConfigDocument.RuleConditionEntry
        {
            Field = SelectedField.ToString(),
            Operator = SelectedOperator.ToString(),
            Value = MessageRuleValidation.NeedsValue(SelectedOperator) ? ValueBox.Text.Trim() : string.Empty,
            HeaderName = MessageRuleValidation.NeedsHeaderName(SelectedField) && HeaderNameBox.Text.Trim().Length > 0
                ? HeaderNameBox.Text.Trim()
                : null,
            Negate = NegateBox.IsChecked == true,
            CaseSensitive = CaseSensitiveBox.IsChecked == true,
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
