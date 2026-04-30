using System.Windows.Controls;
using System.Windows.Input;
using SlurmJobManager.App.ViewModels;

namespace SlurmJobManager.App.Views;

public partial class TaskEditorView : UserControl
{
    private TaskEditorViewModel? Vm => DataContext as TaskEditorViewModel;

    public TaskEditorView()
    {
        InitializeComponent();
    }

    private void TaskFilesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TaskFilesList.SelectedItem is not TaskFileEntry entry || Vm == null)
            return;

        if (Vm.OpenTaskFileCommand.CanExecute(entry))
            Vm.OpenTaskFileCommand.Execute(entry);
    }
}
