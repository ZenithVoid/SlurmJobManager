using System.Windows;
using SlurmJobManager.App.ViewModels.Dialogs;

namespace SlurmJobManager.App.Views.Dialogs;

public partial class TaskValidationDialogView : Window
{
    public TaskValidationDialogView()
    {
        InitializeComponent();
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TaskValidationDialogViewModel vm || !vm.CanContinue)
            return;

        vm.ContinueRequested = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
