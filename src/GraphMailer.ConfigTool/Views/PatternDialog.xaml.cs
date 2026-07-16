using System.Windows;
using System.Windows.Input;

namespace GraphMailer.ConfigTool.Views;

/// <summary>
/// Generic single-field modal with live validation.
/// Usage: new PatternDialog(title, description, initialValue, validate) { Owner = ... }
/// After ShowDialog() == true, read Result.
/// </summary>
public partial class PatternDialog : Window
{
    private readonly Func<string, string?> _validate;

    /// <summary>The trimmed, validated value after the user clicked OK.</summary>
    public string Result { get; private set; } = "";

    /// <param name="title">Window title shown in the title bar.</param>
    /// <param name="description">Short helper text shown above the input field.</param>
    /// <param name="initialValue">Pre-filled value for edit mode; use "" for add mode.</param>
    /// <param name="validate">Returns null when valid, or an error message when invalid.</param>
    public PatternDialog(string title, string description, string initialValue, Func<string, string?> validate)
    {
        _validate = validate;
        InitializeComponent();

        Title = title;
        DescriptionText.Text = description;
        InputBox.Text = initialValue;

        // Position cursor at end for edit mode
        InputBox.CaretIndex = initialValue.Length;

        // Run initial validation so OK is enabled if initialValue is already valid
        Validate(initialValue);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        InputBox.Focus();
        InputBox.SelectAll();
    }

    private void InputBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => Validate(InputBox.Text);

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        // Escape is handled by IsCancel on the Cancel button
    }

    private void Validate(string text)
    {
        var error = _validate(text.Trim());
        if (error is null)
        {
            ErrorText.Visibility = Visibility.Collapsed;
            OkButton.IsEnabled = true;
        }
        else
        {
            ErrorText.Text = error;
            ErrorText.Visibility = string.IsNullOrWhiteSpace(text)
                ? Visibility.Collapsed   // don't show error on empty field before the user has typed
                : Visibility.Visible;
            OkButton.IsEnabled = false;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = InputBox.Text.Trim();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}
