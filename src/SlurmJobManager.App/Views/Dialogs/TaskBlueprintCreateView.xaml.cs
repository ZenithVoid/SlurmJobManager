using System.Windows;
using SlurmJobManager.App.ViewModels.Dialogs;

namespace SlurmJobManager.App.Views.Dialogs;

public partial class TaskBlueprintCreateView : Window
{
    public TaskBlueprintCreateView()
    {
        InitializeComponent();
    }

    private async void BtnCreate_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TaskBlueprintCreateViewModel vm) return;
        if (await vm.ConfirmCreateAsync())
            DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}
