using System.Windows;
using System.Windows.Input;
using SlurmJobManager.App.ViewModels;

namespace SlurmJobManager.App.Views;

public partial class RemoteDirectoryPickerView : Window
{
    private RemoteDirectoryPickerViewModel Vm => (RemoteDirectoryPickerViewModel)DataContext;

    public RemoteDirectoryPickerView()
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

    private void DirListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm.SelectedEntry is not null)
            Vm.NavigateIntoCommand.Execute(Vm.SelectedEntry);
    }

    private async void BtnSelect_Click(object sender, RoutedEventArgs e)
    {
        if (await Vm.TrySelectCurrentAsync())
            DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
