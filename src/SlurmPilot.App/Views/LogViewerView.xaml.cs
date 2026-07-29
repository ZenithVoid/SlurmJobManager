using System.Collections.Specialized;
using System.Windows.Controls;
using SlurmPilot.App.ViewModels;

namespace SlurmPilot.App.Views;

public partial class LogViewerView : UserControl
{
    private LogViewerViewModel? _subscribedVm;

    public LogViewerView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ResubscribeLines();
    }

    private void ResubscribeLines()
    {
        if (_subscribedVm != null)
            _subscribedVm.Lines.CollectionChanged -= OnLinesChanged;

        _subscribedVm = DataContext as LogViewerViewModel;

        if (_subscribedVm != null)
            _subscribedVm.Lines.CollectionChanged += OnLinesChanged;
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add
            && _subscribedVm?.FollowMode == true
            && LogList.Items.Count > 0)
        {
            LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
        }
    }
}
