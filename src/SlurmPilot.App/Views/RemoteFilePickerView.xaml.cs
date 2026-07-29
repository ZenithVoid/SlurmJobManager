using System.Windows;
using System.Windows.Input;
using SlurmPilot.App.ViewModels;

namespace SlurmPilot.App.Views;

public partial class RemoteFilePickerView : Window
{
    private RemoteFilePickerViewModel Vm => (RemoteFilePickerViewModel)DataContext;

    public RemoteFilePickerView()
    {
        InitializeComponent();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (WindowState == WindowState.Maximized)
            WindowState = WindowState.Normal;

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { }
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void BtnClose_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;

    private void FileListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm.SelectedEntry == null) return;

        if (Vm.SelectedEntry.IsDirectory)
        {
            Vm.NavigateIntoCommand.Execute(Vm.SelectedEntry);
            return;
        }

        Vm.SelectFileCommand.Execute(null);
        DialogResult = true;
    }

    private void BtnSelect_Click(object sender, RoutedEventArgs e)
    {
        Vm.SelectFileCommand.Execute(null);
        if (Vm.ResultPath != null)
            DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}
