using System.Windows;
using SlurmJobManager.App.ViewModels.Dialogs;

namespace SlurmJobManager.App.Views.Dialogs;

public partial class TaskBlueprintSaveView : Window
{
    public TaskBlueprintSaveView()
    {
        InitializeComponent();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TaskBlueprintSaveViewModel vm) return;
        vm.SaveCommand.Execute(null);
        if (vm.Confirmed)
            DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}
