using System.Collections.Specialized;
using System.Windows.Controls;
using SlurmJobManager.App.ViewModels;

namespace SlurmJobManager.App.Views;

public partial class LogViewerView : UserControl
{
    public LogViewerView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => SubscribeLines();
    }

    private void SubscribeLines()
    {
        if (DataContext is LogViewerViewModel vm)
        {
            vm.Lines.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add && vm.FollowMode
                    && LogList.Items.Count > 0)
                    LogList.ScrollIntoView(LogList.Items[^1]);
            };
        }
    }
}
