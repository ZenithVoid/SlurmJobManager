using System.Windows;
using System.Windows.Input;
using SlurmPilot.App.ViewModels.Dialogs;

namespace SlurmPilot.App.Views.Dialogs;

public partial class TaskBlueprintCreateView : Window
{
    public TaskBlueprintCreateView()
    {
        InitializeComponent();
        StateChanged += (_, _) => UpdateMaximizeIcon();
        Loaded += (_, _) => UpdateMaximizeIcon();
    }

    private async void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TaskBlueprintCreateViewModel vm) return;
        if (await vm.ConfirmApplyAsync())
            DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        => ToggleMaximizeRestore();

    private void BtnClose_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void UpdateMaximizeIcon()
    {
        if (MaximizeIcon is null)
            return;

        MaximizeIcon.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }
}
