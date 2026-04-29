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

    private void DirListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm.SelectedEntry is not null)
            Vm.NavigateIntoCommand.Execute(Vm.SelectedEntry);
    }

    private void BtnSelect_Click(object sender, RoutedEventArgs e)
    {
        Vm.SelectCurrentCommand.Execute(null);
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
