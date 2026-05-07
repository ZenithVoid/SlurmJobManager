using System.Windows;
using SlurmJobManager.App.ViewModels.Dialogs;

namespace SlurmJobManager.App.Views.Dialogs;

public partial class NameInputDialogView : Window
{
    public NameInputDialogView()
    {
        InitializeComponent();
        Loaded += (_, _) => NameTextBox.Focus();
    }

    private void BtnConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not NameInputDialogViewModel vm)
            return;

        vm.Confirm();
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}
