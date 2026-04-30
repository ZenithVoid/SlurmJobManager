using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SlurmJobManager.App.Services;
using SlurmJobManager.App.ViewModels;

namespace SlurmJobManager.App;

public partial class MainWindow : Window
{
    // Map tab IDs to page grids and their ScaleTransforms
    private IReadOnlyDictionary<string, (System.Windows.Controls.Grid Page, ScaleTransform Scale)>? _pages;

    public MainWindow()
    {
        InitializeComponent();
        StateChanged += MainWindow_StateChanged;
        Loaded        += MainWindow_Loaded;
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
        if (DataContext is MainViewModel vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.ActiveTab))
                    AnimatePageIn(vm.ActiveTab);
            };

            // Animate the initial page
            AnimatePageIn(vm.ActiveTab);
        }
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

    // ── B5: Task file list double-click handler ───────────────────────────

    private void TaskFileListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox lb && lb.SelectedItem is TaskFileEntry fileEntry
            && DataContext is MainViewModel vm)
        {
            vm.TaskEditor.OpenTaskFileCommand.Execute(fileEntry);
        }
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
}
