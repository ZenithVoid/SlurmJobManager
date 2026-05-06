using System.Windows;

namespace SlurmJobManager.App.Views.Dialogs;

public partial class ConfirmationDialogView : Window
{
    public ConfirmationDialogView()
    {
        InitializeComponent();
    }

    private void BtnConfirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
