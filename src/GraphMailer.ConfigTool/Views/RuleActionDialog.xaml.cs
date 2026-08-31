using System.Windows;
using System.Windows.Controls;
using GraphMailer.ConfigTool.Helpers;
using GraphMailer.Service.Configuration;
using GraphMailer.Service.Infrastructure.Config;
using GraphMailer.Service.Infrastructure.Rules;

namespace GraphMailer.ConfigTool.Views;

/// <summary>
/// Adds or edits one action of a rule.
///
/// Only the fields the selected action type actually uses are shown, driven by the service's own
/// <c>RuleActionSchema</c> — the same table the validator and the config writer use, so the form
/// can never offer a value the runtime ignores.
/// </summary>
public partial class RuleActionDialog : Window
{
    private bool _loading = true;

    /// <summary>The edited action, valid only when the dialog returned true.</summary>
    internal ConfigDocument.RuleActionEntry Result { get; private set; } = new();

    internal RuleActionDialog(ConfigDocument.RuleActionEntry? existing = null)
    {
        InitializeComponent();
        Title = existing is null ? "Add action" : "Edit action";

        foreach (var type in Enum.GetValues<RuleActionType>())
            TypeBox.Items.Add(type.ToString());

        var current = existing ?? new ConfigDocument.RuleActionEntry();
        TypeBox.SelectedItem = current.Type;
        if (TypeBox.SelectedIndex < 0) TypeBox.SelectedIndex = 0;

        ValueBox.Text = current.Value ?? string.Empty;
        MultilineValueBox.Text = current.Value ?? string.Empty;
        HtmlBox.Text = current.Html ?? string.Empty;
        HeaderNameBox.Text = current.HeaderName ?? string.Empty;
        MatchBox.Text = current.Match ?? string.Empty;
        SmtpCodeBox.Text = current.SmtpCode?.ToString() ?? string.Empty;
        SelectByTag(RecipientBox, current.Recipient ?? "To");
        SelectByTag(AttachmentMatchBox, current.AttachmentMatch ?? "NamePattern");

        _loading = false;
        UpdateFieldVisibility();
        Validate();
    }

    private RuleActionType SelectedType
        => MessageRuleModel.Parse(TypeBox.SelectedItem as string, RuleActionType.PrefixSubject);

    /// <summary>Body snippets are multi-line; everything else is a single value.</summary>
    private bool UsesMultilineValue
        => SelectedType is RuleActionType.PrependBody or RuleActionType.AppendBody;

    /// <summary>
    /// The value as it will be stored.
    ///
    /// Trimmed for everything the action treats as a token — an address, a header name, a
    /// pattern. <b>Not</b> trimmed for the actions that splice the text into the message
    /// verbatim: a subject prefix of <c>"[EXTERNAL] "</c> needs its trailing space, and body
    /// text needs its indentation and blank lines. See <c>RuleActionSchema.PreservesWhitespace</c>.
    /// </summary>
    private string CurrentValue
    {
        get
        {
            var raw = UsesMultilineValue ? MultilineValueBox.Text : ValueBox.Text;
            return RuleActionSchema.PreservesWhitespace(SelectedType) ? raw : raw.Trim();
        }
    }

    private static void SelectByTag(ComboBox box, string value)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            var token = item.Tag as string ?? item.Content as string ?? string.Empty;
            if (token.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    private static string SelectedTag(ComboBox box)
    {
        if (box.SelectedItem is ComboBoxItem item)
            return item.Tag as string ?? item.Content as string ?? string.Empty;
        return string.Empty;
    }

    private void Type_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        UpdateFieldVisibility();
        Validate();
    }

    private void Field_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        UpdateFieldVisibility();
        Validate();
    }

    private void UpdateFieldVisibility()
    {
        var type = SelectedType;

        Show(RecipientLabel, RecipientBox, MessageRuleValidation.ActionUses(type, RuleActionParam.Recipient));
        Show(MatchLabel, MatchBox, MessageRuleValidation.ActionUses(type, RuleActionParam.Match));
        Show(HeaderNameLabel, HeaderNameBox, MessageRuleValidation.ActionUses(type, RuleActionParam.HeaderName));
        Show(AttachmentMatchLabel, AttachmentMatchBox, MessageRuleValidation.ActionUses(type, RuleActionParam.AttachmentMatch));
        Show(HtmlLabel, HtmlBox, MessageRuleValidation.ActionUses(type, RuleActionParam.Html));
        Show(SmtpCodeLabel, SmtpCodeBox, MessageRuleValidation.ActionUses(type, RuleActionParam.SmtpCode));

        var usesValue = MessageRuleValidation.ActionUses(type, RuleActionParam.Value);
        ValueLabel.Visibility = usesValue ? Visibility.Visible : Visibility.Collapsed;
        ValueLabel.Text = ValueCaption(type);
        ValueBox.Visibility = usesValue && !UsesMultilineValue ? Visibility.Visible : Visibility.Collapsed;
        MultilineValueBox.Visibility = usesValue && UsesMultilineValue ? Visibility.Visible : Visibility.Collapsed;

        DescriptionText.Text = Describe(type);
    }

    private static void Show(UIElement label, UIElement field, bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        label.Visibility = visibility;
        field.Visibility = visibility;
    }

    /// <summary>The value field means something different for every action — say which.</summary>
    private static string ValueCaption(RuleActionType type) => type switch
    {
        RuleActionType.Reject => "Text sent to the sending application (optional)",
        RuleActionType.AddRecipient => "Address to add",
        RuleActionType.ReplaceRecipient => "New address",
        RuleActionType.SetSubject => "New subject",
        RuleActionType.PrefixSubject => "Text to put in front of the subject",
        RuleActionType.SuffixSubject => "Text to append to the subject",
        RuleActionType.PrependBody => "Text to put above the message body",
        RuleActionType.AppendBody => "Text to put below the message body",
        RuleActionType.SetHeader or RuleActionType.AddHeader => "Header value",
        RuleActionType.RemoveAttachments => "Pattern, extension list or size",
        RuleActionType.SetImportance => "Low, Normal or High",
        RuleActionType.SetFrom => "New From address",
        RuleActionType.SetReplyTo => "New Reply-To address",
        _ => "Value",
    };

    private static string Describe(RuleActionType type) => type switch
    {
        RuleActionType.Reject =>
            "Refuses the message during the SMTP session. The sending application sees the reply "
            + "code and text below, and nothing is queued.",
        RuleActionType.Discard =>
            "Accepts the message and then throws it away. The sending application is told it was "
            + "delivered.",
        RuleActionType.AddRecipient =>
            "Adds a recipient. Bcc is added to the envelope only, so the address does not appear "
            + "in the message.",
        RuleActionType.RemoveRecipient =>
            "Removes matching recipients from the message and from the envelope, so they are no "
            + "longer delivered to.",
        RuleActionType.ReplaceRecipient =>
            "Removes a recipient and adds another one in its place.",
        RuleActionType.PrependBody or RuleActionType.AppendBody =>
            "Adds text to the message body. The plain-text and HTML versions are handled "
            + "separately; a body the message does not have is not created.",
        RuleActionType.SetHeader =>
            "Sets a header, replacing every existing occurrence.",
        RuleActionType.AddHeader =>
            "Adds another occurrence of a header, leaving existing ones in place.",
        RuleActionType.RemoveHeader =>
            "Removes every occurrence of a header.",
        RuleActionType.RemoveAttachments =>
            "Removes matching attachments. Images embedded in the message body are kept — "
            + "removing them would leave the body visibly broken.",
        RuleActionType.SetImportance =>
            "Sets the message importance, which Microsoft 365 carries through to the recipient.",
        RuleActionType.SetFrom =>
            "Changes the sender. This also changes which mailbox the message is sent from.",
        RuleActionType.SetReplyTo =>
            "Sets the Reply-To address without changing the sender.",
        _ => string.Empty,
    };

    private ConfigDocument.RuleActionEntry Collect()
    {
        var type = SelectedType;

        return new ConfigDocument.RuleActionEntry
        {
            Type = type.ToString(),
            Value = MessageRuleValidation.ActionUses(type, RuleActionParam.Value) ? CurrentValue : null,
            // Blank HTML means "derive it from the text"; anything else is markup the operator
            // wrote and is stored as typed.
            Html = MessageRuleValidation.ActionUses(type, RuleActionParam.Html)
                   && !string.IsNullOrWhiteSpace(HtmlBox.Text)
                ? HtmlBox.Text
                : null,
            HeaderName = MessageRuleValidation.ActionUses(type, RuleActionParam.HeaderName)
                ? HeaderNameBox.Text.Trim()
                : null,
            Recipient = MessageRuleValidation.ActionUses(type, RuleActionParam.Recipient)
                ? SelectedTag(RecipientBox)
                : null,
            Match = MessageRuleValidation.ActionUses(type, RuleActionParam.Match) ? MatchBox.Text.Trim() : null,
            AttachmentMatch = MessageRuleValidation.ActionUses(type, RuleActionParam.AttachmentMatch)
                ? SelectedTag(AttachmentMatchBox)
                : null,
            SmtpCode = MessageRuleValidation.ActionUses(type, RuleActionParam.SmtpCode)
                       && int.TryParse(SmtpCodeBox.Text.Trim(), out var code)
                ? code
                : null,
        };
    }

    private void Validate(object? sender = null, RoutedEventArgs? e = null)
    {
        if (_loading) return;

        var type = SelectedType;
        var entry = Collect();

        // A reply code that is not a number at all is caught here; out-of-range values are the
        // validator's business, so both paths report the same wording.
        if (MessageRuleValidation.ActionUses(type, RuleActionParam.SmtpCode)
            && SmtpCodeBox.Text.Trim().Length > 0
            && !int.TryParse(SmtpCodeBox.Text.Trim(), out _))
        {
            SetError("An SMTP rejection code is between 400 and 599.");
            return;
        }

        SetError(MessageRuleValidation.ValidateAction(type, entry));

        var warning = MessageRuleValidation.DescribeActionWarning(type, entry);
        WarningText.Text = warning ?? string.Empty;
        WarningBox.Visibility = warning is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetError(string? message)
    {
        ErrorText.Text = message ?? string.Empty;
        ErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = message is null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = Collect();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
