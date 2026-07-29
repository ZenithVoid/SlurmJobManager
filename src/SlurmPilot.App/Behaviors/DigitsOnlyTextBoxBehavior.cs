using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SlurmPilot.App.Behaviors;

public static class DigitsOnlyTextBoxBehavior
{
    private static readonly Regex DigitsOnlyRegex = new(@"^\d*$", RegexOptions.Compiled);

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(DigitsOnlyTextBoxBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) =>
        (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value) =>
        obj.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox textBox)
            return;

        if ((bool)e.NewValue)
        {
            textBox.PreviewTextInput += OnPreviewTextInput;
            DataObject.AddPastingHandler(textBox, OnPasting);
        }
        else
        {
            textBox.PreviewTextInput -= OnPreviewTextInput;
            DataObject.RemovePastingHandler(textBox, OnPasting);
        }
    }

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not TextBox textBox)
            return;

        e.Handled = !IsValid(textBox, e.Text);
    }

    private static void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox textBox)
            return;

        if (!e.DataObject.GetDataPresent(DataFormats.Text))
        {
            e.CancelCommand();
            return;
        }

        var pastedText = e.DataObject.GetData(DataFormats.Text) as string ?? string.Empty;
        if (!IsValid(textBox, pastedText))
            e.CancelCommand();
    }

    private static bool IsValid(TextBox textBox, string input)
    {
        var currentText = textBox.Text ?? string.Empty;
        var nextText = currentText.Remove(textBox.SelectionStart, textBox.SelectionLength)
            .Insert(textBox.SelectionStart, input);

        return DigitsOnlyRegex.IsMatch(nextText);
    }
}
