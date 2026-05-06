using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SlurmJobManager.App.Services;
using SlurmJobManager.App.ViewModels;

namespace SlurmJobManager.App;

public partial class MainWindow : Window
{
    // Map tab IDs to page grids and their ScaleTransforms
    private IReadOnlyDictionary<string, (System.Windows.Controls.Grid Page, ScaleTransform Scale)>? _pages;
    private MainViewModel? _subscribedVm;

    public MainWindow()
    {
        InitializeComponent();
        StateChanged += MainWindow_StateChanged;
        Loaded        += MainWindow_Loaded;
        DataContextChanged += (_, _) => SubscribeToViewModel();
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Wire toast container to the service instance
        ToastContainer.DataContext = ToastService.Instance.Toasts;

        // Build the page lookup after InitializeComponent so named elements exist
        _pages = new Dictionary<string, (System.Windows.Controls.Grid, ScaleTransform)>
        {
            { "Dashboard", (PageDashboard, ScaleDashboard) },
            { "Tasks",     (PageTasks,     ScaleTasks) },
            { "Monitor",   (PageMonitor,   ScaleMonitor) },
            { "Logs",      (PageLogs,      ScaleLogs) },
            { "Console",   (PageConsole,   ScaleConsole) },
            { "Settings",  (PageSettings,  ScaleSettings) },
        };

        // Subscribe to navigation changes for page transitions
        SubscribeToViewModel();
    }

    // ── Page transition animation ─────────────────────────────────────────

    private void AnimatePageIn(string tabId)
    {
        if (_pages is null || !_pages.TryGetValue(tabId, out var entry)) return;
        var (page, scale) = entry;

        // Reset to starting state (invisible, slightly shrunk)
        page.Opacity = 0;
        scale.ScaleX = 0.97;
        scale.ScaleY = 0.97;

        var duration = new Duration(TimeSpan.FromMilliseconds(200));
        var ease     = new CubicEase { EasingMode = EasingMode.EaseOut };

        var fadeIn = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
        var scaleX = new DoubleAnimation(0.97, 1.0, duration) { EasingFunction = ease };
        var scaleY = new DoubleAnimation(0.97, 1.0, duration) { EasingFunction = ease };

        page.BeginAnimation(OpacityProperty, fadeIn);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
    }

    private void TaskFileListItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem item
            || item.DataContext is not TaskFileEntry fileEntry
            || DataContext is not MainViewModel vm)
            return;

        if (ItemsControl.ItemsControlFromItemContainer(item) is not ListBox listBox)
            return;

        listBox.SelectedItem = fileEntry;

        if (!vm.TaskEditor.OpenTaskFileCommand.CanExecute(fileEntry))
            return;

        vm.TaskEditor.OpenTaskFileCommand.Execute(fileEntry);
        e.Handled = true;
    }

    private void TaskFileListItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem item)
            return;

        if (ItemsControl.ItemsControlFromItemContainer(item) is not ListBox listBox)
            return;

        if (!item.IsSelected)
        {
            listBox.SelectedItems.Clear();
            item.IsSelected = true;
        }

        listBox.SelectedItem = item.DataContext;
    }

    private void TaskFileListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        var hit = e.OriginalSource as DependencyObject;
        if (FindAncestor<ListBoxItem>(hit) != null)
            return;

        listBox.SelectedItems.Clear();
        listBox.SelectedItem = null;
    }

    private void TaskFileContextMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.PlacementTarget is not ListBox listBox)
        {
            e.Handled = true;
            return;
        }

        var selectedCount = listBox.SelectedItems.Count;
        if (selectedCount <= 0)
        {
            e.Handled = true;
            return;
        }

        var hasSingleSelection = selectedCount == 1 && listBox.SelectedItem is TaskFileEntry;
        if (menu.FindName("TaskFileContextOpenMenuItem") is MenuItem openItem)
            openItem.IsEnabled = hasSingleSelection;
        if (menu.FindName("TaskFileContextTimeInfoMenuItem") is MenuItem timeInfoItem)
            timeInfoItem.IsEnabled = hasSingleSelection;
        if (menu.FindName("TaskFileContextDeleteMenuItem") is MenuItem deleteItem)
            deleteItem.IsEnabled = selectedCount > 0;
    }

    private void TaskFileContextOpen_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || TaskFileListBox.SelectedItem is not TaskFileEntry fileEntry)
            return;

        if (!vm.TaskEditor.OpenTaskFileCommand.CanExecute(fileEntry))
            return;

        vm.TaskEditor.OpenTaskFileCommand.Execute(fileEntry);
    }

    private void TaskFileContextDelete_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        var selectedEntries = TaskFileListBox.SelectedItems
            .OfType<TaskFileEntry>()
            .ToList();
        if (selectedEntries.Count == 0)
            return;

        if (!vm.TaskEditor.DeleteTaskFilesCommand.CanExecute(selectedEntries))
            return;

        vm.TaskEditor.DeleteTaskFilesCommand.Execute(selectedEntries);
    }

    private void TaskFileContextViewTimeInfo_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || TaskFileListBox.SelectedItem is not TaskFileEntry fileEntry)
            return;

        if (!vm.TaskEditor.ViewTaskFileTimeInfoCommand.CanExecute(fileEntry))
            return;

        vm.TaskEditor.ViewTaskFileTimeInfoCommand.Execute(fileEntry);
    }

    private void ApplyTabFocus(string tabId)
    {
        Dispatcher.BeginInvoke(() =>
        {
            switch (tabId)
            {
                case "Console":
                    PageConsole.Focus();
                    Keyboard.Focus(PageConsole);
                    ConsolePageView.RequestTerminalFocus();
                    Dispatcher.BeginInvoke(() => ConsolePageView.RequestTerminalFocus(), DispatcherPriority.ContextIdle);
                    break;
                case "Tasks":
                    PageTasks.Focus();
                    Keyboard.Focus(PageTasks);
                    Dispatcher.BeginInvoke(() => Keyboard.Focus(PageTasks), DispatcherPriority.ContextIdle);
                    break;
            }
        }, DispatcherPriority.Input);
    }

    private void SubscribeToViewModel()
    {
        if (_subscribedVm != null)
            _subscribedVm.PropertyChanged -= OnMainViewModelPropertyChanged;

        _subscribedVm = DataContext as MainViewModel;
        if (_subscribedVm == null)
            return;

        _subscribedVm.PropertyChanged += OnMainViewModelPropertyChanged;
        AnimatePageIn(_subscribedVm.ActiveTab);
        ApplyTabFocus(_subscribedVm.ActiveTab);
    }

    private void OnMainViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (sender is not MainViewModel vm) return;
        if (args.PropertyName != nameof(MainViewModel.ActiveTab)) return;

        AnimatePageIn(vm.ActiveTab);
        ApplyTabFocus(vm.ActiveTab);
    }

    // ── Custom title-bar interactions ─────────────────────────────────────

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
        }
        else
        {
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
            DragMove();
        }
    }

    private void TitleBar_MouseMove(object sender, MouseEventArgs e)
    {
        // DragMove in MouseLeftButtonDown handles movement; no additional action needed.
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        => ToggleMaximize();

    private void BtnClose_Click(object sender, RoutedEventArgs e)
        => Close();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeIcon != null)
        {
            MaximizeIcon.Text = WindowState == WindowState.Maximized
                ? "\uE923"
                : "\uE922";
        }

        if (BtnMaximize != null)
        {
            var tooltipKey = WindowState == WindowState.Maximized
                ? "TitleBar.Restore"
                : "TitleBar.Maximize";
            BtnMaximize.ToolTip = Application.Current?.TryFindResource(tooltipKey) as string
                                  ?? (WindowState == WindowState.Maximized ? "Restore" : "Maximize");
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        if (_subscribedVm != null)
            _subscribedVm.PropertyChanged -= OnMainViewModelPropertyChanged;
        _subscribedVm = null;
    }

    private static T? FindAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T matched)
                return matched;

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }
}
