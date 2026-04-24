using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Input;
using SlurmJobManager.App.ViewModels;

namespace SlurmJobManager.App.Views;

public partial class ConsoleView : UserControl
{
    private ConsoleViewModel? _subscribedVm;

    public ConsoleView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ResubscribeOutput();
    }

    private void ResubscribeOutput()
    {
        if (_subscribedVm != null)
            _subscribedVm.OutputLines.CollectionChanged -= OnOutputLinesChanged;

        _subscribedVm = DataContext as ConsoleViewModel;

        if (_subscribedVm != null)
            _subscribedVm.OutputLines.CollectionChanged += OnOutputLinesChanged;
    }

    private void OnOutputLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && OutputList.Items.Count > 0)
            OutputList.ScrollIntoView(OutputList.Items[OutputList.Items.Count - 1]);
    }

    private void CmdInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ConsoleViewModel vm) return;

        switch (e.Key)
        {
            case Key.Enter:
                vm.ExecuteCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
                vm.HistoryUpCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Down:
                vm.HistoryDownCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
