using System.Windows;

namespace SlurmJobManager.App.Views.Dialogs;

public partial class ConfirmationDialogView : Window
{
    /// <summary>
    /// True when the optional "discard" button was clicked (as opposed to the confirm button).
    /// Only meaningful when <c>ShowDialog()</c> returned <c>true</c>.
    /// </summary>
    public bool DiscardChosen { get; private set; }

    public ConfirmationDialogView()
    {
        InitializeComponent();
    }

    private void BtnConfirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void BtnDiscard_Click(object sender, RoutedEventArgs e)
    {
        DiscardChosen = true;
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
