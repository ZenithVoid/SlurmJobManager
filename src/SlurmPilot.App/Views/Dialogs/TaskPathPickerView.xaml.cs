using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SlurmPilot.App.ViewModels.Dialogs;

namespace SlurmPilot.App.Views.Dialogs;

public partial class TaskPathPickerView : Window
{
    public TaskPathPickerView()
    {
        InitializeComponent();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
        else if (WindowState == WindowState.Normal || WindowState == WindowState.Maximized)
        {
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
            DragMove();
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void BtnClose_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;

    private void PathListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => TryConfirmSelected();

    private void BtnSelect_Click(object sender, RoutedEventArgs e)
        => TryConfirmSelected();

    private void BtnUseManual_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is TaskPathPickerViewModel vm && vm.ConfirmManual())
            DialogResult = true;
    }

    private void BtnBrowseRemote_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is TaskPathPickerViewModel vm && vm.RequestBrowse())
            DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;

    private void TryConfirmSelected()
    {
        if (DataContext is TaskPathPickerViewModel vm && vm.ConfirmSelected())
            DialogResult = true;
    }
}
