using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GraphMailer.ConfigTool.Helpers;

/// <summary>
/// Attached behavior for numeric-only TextBox fields.
/// Usage in XAML:
///   xmlns:h="clr-namespace:GraphMailer.ConfigTool.Helpers"
///   &lt;TextBox h:NumericField.Min="1" .../>
///   &lt;TextBox h:NumericField.Min="1" h:NumericField.Max="100" .../>
///
/// When focus leaves a field whose current value is invalid the text is
/// automatically reverted to the last accepted valid value.
/// </summary>
public static class NumericField
{
    // ── Attached properties ───────────────────────────────────────────────

    public static readonly DependencyProperty MinProperty =
        DependencyProperty.RegisterAttached("Min", typeof(int), typeof(NumericField),
            new PropertyMetadata(int.MinValue, OnMinChanged));

    public static readonly DependencyProperty MaxProperty =
        DependencyProperty.RegisterAttached("Max", typeof(int), typeof(NumericField),
            new PropertyMetadata(int.MaxValue, OnMaxChanged));

    /// <summary>Read-only: true when the current value violates Min/Max.</summary>
    public static readonly DependencyProperty HasErrorProperty =
        DependencyProperty.RegisterAttached("HasError", typeof(bool), typeof(NumericField),
            new PropertyMetadata(false));

    public static int GetMin(DependencyObject d) => (int)d.GetValue(MinProperty);
    public static void SetMin(DependencyObject d, int v) => d.SetValue(MinProperty, v);

    public static int GetMax(DependencyObject d) => (int)d.GetValue(MaxProperty);
    public static void SetMax(DependencyObject d, int v) => d.SetValue(MaxProperty, v);

    public static bool GetHasError(DependencyObject d) => (bool)d.GetValue(HasErrorProperty);
    private static void SetHasError(DependencyObject d, bool v) => d.SetValue(HasErrorProperty, v);

    // ── Last-valid tracking ───────────────────────────────────────────────

    // Weak reference so we don't prevent GC of removed TextBoxes
    private static readonly ConditionalWeakTable<TextBox, StrongBox<string>> _lastValid = new();

    // ── Attachment ────────────────────────────────────────────────────────

    private static readonly HashSet<TextBox> _attached = [];

    private static void OnMinChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb) return;
        Attach(tb);
        tb.Loaded += (_, _) => Validate(tb);
    }

    private static void OnMaxChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBox tb && _attached.Contains(tb))
            Validate(tb);
    }

    private static void Attach(TextBox tb)
    {
        if (!_attached.Add(tb)) return;
        tb.PreviewTextInput += OnPreviewTextInput;
        DataObject.AddPastingHandler(tb, OnPasting);
        tb.TextChanged += OnTextChanged;
        tb.LostFocus += OnLostFocus;
    }

    // ── Handlers ──────────────────────────────────────────────────────────

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = !e.Text.All(char.IsDigit);

    private static void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(typeof(string)))
        {
            var text = (string)e.DataObject.GetData(typeof(string))!;
            if (!text.All(char.IsDigit)) e.CancelCommand();
        }
        else e.CancelCommand();
    }

    private static void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb) Validate(tb);
    }

    private static void OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb || !GetHasError(tb)) return;
        if (_lastValid.TryGetValue(tb, out var box) && box.Value is not null)
        {
            tb.Text = box.Value;
            tb.CaretIndex = tb.Text.Length;
        }
    }

    // ── Validation ────────────────────────────────────────────────────────

    internal static void Validate(TextBox tb)
    {
        var text = tb.Text.Trim();
        var min = GetMin(tb);
        var max = GetMax(tb);

        bool valid = int.TryParse(text, out var value)
            && (min == int.MinValue || value >= min)
            && (max == int.MaxValue || value <= max);

        if (valid)
            _lastValid.GetOrCreateValue(tb).Value = text;

        SetHasError(tb, !valid);

        var dangerBrush = tb.TryFindResource("DangerBrush") as Brush;
        var normalBrush = tb.TryFindResource("BorderBrush") as Brush;

        if (dangerBrush is not null && normalBrush is not null)
            tb.BorderBrush = valid ? normalBrush : dangerBrush;
    }

    // ── Helper: scan a subtree for any field with HasError = true ─────────

    /// <summary>
    /// Returns true if any descendant TextBox in <paramref name="root"/>
    /// has a NumericField validation error.
    /// </summary>
    public static bool SubtreeHasErrors(DependencyObject root)
    {
        if (GetHasError(root)) return true;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (SubtreeHasErrors(child)) return true;
        }
        return false;
    }
}
