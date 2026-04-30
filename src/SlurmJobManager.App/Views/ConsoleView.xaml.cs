using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SlurmJobManager.App.ViewModels;

namespace SlurmJobManager.App.Views;

public partial class ConsoleView : UserControl
{
    private ConsoleViewModel? _subscribedVm;
    private bool _suppressScrollTracking;

    public ConsoleView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ResubscribeOutput();
        Loaded += ConsoleView_Loaded;
    }

    private void ConsoleView_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        OutputScrollViewer.ScrollChanged += OutputScrollViewer_ScrollChanged;
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
        if (e.Action != NotifyCollectionChangedAction.Add || OutputList.Items.Count == 0)
            return;

        if (DataContext is not ConsoleViewModel vm || !vm.IsAutoScrollEnabled)
            return;

        _suppressScrollTracking = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            OutputScrollViewer.ScrollToEnd();
            _suppressScrollTracking = false;
        });
    }

    private void OutputScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_suppressScrollTracking || DataContext is not ConsoleViewModel vm)
            return;

        if (e.ExtentHeightChange == 0)
        {
            var atBottom = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 2;
            vm.IsAutoScrollEnabled = atBottom;
        }
    }

    private void JumpToBottom_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not ConsoleViewModel vm)
            return;
        vm.IsAutoScrollEnabled = true;
        _suppressScrollTracking = true;
        OutputScrollViewer.ScrollToEnd();
        _suppressScrollTracking = false;
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
