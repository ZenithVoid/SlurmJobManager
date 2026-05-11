using System.Windows;
using System.Windows.Input;
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

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void BtnClose_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}
