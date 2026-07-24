using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SlurmJobManager.App.Services;
using SlurmJobManager.App.ViewModels;
using WF = System.Windows.Forms;

namespace SlurmJobManager.App;

public partial class MainWindow : Window
{
    // Map tab IDs to page grids and their ScaleTransforms
    private IReadOnlyDictionary<string, (System.Windows.Controls.Grid Page, ScaleTransform Scale)>? _pages;
    private MainViewModel? _subscribedVm;
    private WF.NotifyIcon? _trayIcon;
    private WF.ContextMenuStrip? _trayMenu;
    private bool _hasShownTrayHint;

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
            { "About",     (PageAbout,     ScaleAbout) },
        };

        // Subscribe to navigation changes for page transitions
        SubscribeToViewModel();
        EnsureTrayIcon();
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

    private void TaskFileListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left
            || sender is not ListBox listBox
            || DataContext is not MainViewModel vm)
            return;

        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not { DataContext: TaskFileEntry fileEntry })
            return;

        OpenTaskFileFromListItem(listBox, vm, fileEntry, e);
    }

    private void TaskFileListItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left
            || e.ClickCount != 2
            || sender is not ListBoxItem { DataContext: TaskFileEntry fileEntry } item
            || DataContext is not MainViewModel vm)
            return;

        if (ItemsControl.ItemsControlFromItemContainer(item) is not ListBox listBox)
            return;

        OpenTaskFileFromListItem(listBox, vm, fileEntry, e);
    }

    private void TaskFileListItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem item)
            return;

        if (ItemsControl.ItemsControlFromItemContainer(item) is not ListBox listBox)
            return;

        var hasSelectionModifier = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != ModifierKeys.None;
        if (!item.IsSelected)
        {
            if (!hasSelectionModifier)
                listBox.SelectedItems.Clear();
            item.IsSelected = true;
        }

        if (listBox.SelectedItems.Count == 1)
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

    private void TaskFileContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.PlacementTarget is not ListBox listBox)
        {
            if (sender is ContextMenu unexpectedMenu)
                unexpectedMenu.IsOpen = false;
            return;
        }

        var selectedCount = listBox.SelectedItems.Count;

        var hasSingleSelection = selectedCount == 1 && listBox.SelectedItem is TaskFileEntry;
        var selectedEntry = listBox.SelectedItem as TaskFileEntry;
        if (menu.FindName("TaskFileContextOpenMenuItem") is MenuItem openItem)
            openItem.IsEnabled = hasSingleSelection;
        if (menu.FindName("TaskFileContextRenameMenuItem") is MenuItem renameItem)
            renameItem.IsEnabled = hasSingleSelection;
        if (menu.FindName("TaskFileContextDownloadMenuItem") is MenuItem downloadItem)
            downloadItem.IsEnabled = hasSingleSelection && selectedEntry is { IsDirectory: false };
        if (menu.FindName("TaskFileContextTimeInfoMenuItem") is MenuItem timeInfoItem)
            timeInfoItem.IsEnabled = hasSingleSelection;
        if (menu.FindName("TaskFileContextDeleteMenuItem") is MenuItem deleteItem)
            deleteItem.IsEnabled = selectedCount > 0;
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
        DisposeTrayIcon();
    }

    public void MinimizeToTray()
    {
        EnsureTrayIcon();
        ShowInTaskbar = false;
        WindowState = WindowState.Minimized;
        Hide();

        if (_trayIcon == null)
            return;

        _trayIcon.Visible = true;
        UpdateTrayText();
        if (!_hasShownTrayHint)
        {
            _hasShownTrayHint = true;
            _trayIcon.ShowBalloonTip(
                2500,
                L("Tray.HiddenTitle"),
                L("Tray.HiddenText"),
                WF.ToolTipIcon.Info);
        }
    }

    public void RestoreFromTray()
    {
        EnsureTrayIcon();
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Focus();

        if (_trayIcon != null)
            _trayIcon.Visible = false;
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon != null)
        {
            UpdateTrayText();
            return;
        }

        _trayMenu = new WF.ContextMenuStrip();
        var openItem = new WF.ToolStripMenuItem();
        openItem.Click += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        var exitItem = new WF.ToolStripMenuItem();
        exitItem.Click += (_, _) => Dispatcher.Invoke(() =>
        {
            RestoreFromTray();
            (Application.Current as App)?.RequestApplicationExit(this);
        });
        _trayMenu.Items.Add(openItem);
        _trayMenu.Items.Add(new WF.ToolStripSeparator());
        _trayMenu.Items.Add(exitItem);

        _trayIcon = new WF.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            ContextMenuStrip = _trayMenu,
            Visible = false,
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        UpdateTrayText();
    }

    private void UpdateTrayText()
    {
        if (_trayIcon == null)
            return;

        _trayIcon.Text = L("Tray.ToolTip");
        if (_trayMenu?.Items.Count >= 3)
        {
            _trayMenu.Items[0].Text = L("Tray.Open");
            _trayMenu.Items[2].Text = L("Tray.Exit");
        }
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(processPath);
                if (icon != null)
                    return icon;
            }
        }
        catch
        {
            // Best effort: fall back to the stock application icon.
        }

        return System.Drawing.SystemIcons.Application;
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayMenu?.Dispose();
        _trayMenu = null;
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

    private static void OpenTaskFileFromListItem(ListBox listBox, MainViewModel vm, TaskFileEntry fileEntry, MouseButtonEventArgs e)
    {
        if (!Equals(listBox.SelectedItem, fileEntry))
            listBox.SelectedItem = fileEntry;

        if (!vm.TaskEditor.OpenTaskFileCommand.CanExecute(fileEntry))
            return;

        vm.TaskEditor.OpenTaskFileCommand.Execute(fileEntry);
        e.Handled = true;
    }

    private static string L(string key)
        => Application.Current?.TryFindResource(key) as string ?? key;
}
