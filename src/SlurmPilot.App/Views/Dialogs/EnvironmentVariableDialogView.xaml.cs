using System.Windows;
using SlurmPilot.App.ViewModels.Dialogs;

namespace SlurmPilot.App.Views.Dialogs;

public partial class EnvironmentVariableDialogView : Window
{
    public EnvironmentVariableDialogView()
    {
        InitializeComponent();
        Loaded += (_, _) => KeyTextBox.Focus();
    }

    private void BtnConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not EnvironmentVariableDialogViewModel vm || !vm.CanConfirm)
            return;

        vm.Confirm();
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}
