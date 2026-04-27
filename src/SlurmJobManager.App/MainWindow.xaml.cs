using System.Windows;
using System.Windows.Input;

namespace SlurmJobManager.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        StateChanged += MainWindow_StateChanged;
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
            // Allow drag-move; only start if not maximized or let OS handle snapping
            if (WindowState == WindowState.Maximized)
            {
                // Restore to normal before dragging so position is sensible
                WindowState = WindowState.Normal;
            }
            DragMove();
        }
    }

    private void TitleBar_MouseMove(object sender, MouseEventArgs e)
    {
        // No additional action needed; DragMove handles movement after press.
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
        // Swap the maximize/restore icon based on window state
        if (MaximizeIcon != null)
        {
            MaximizeIcon.Text = WindowState == WindowState.Maximized
                ? "\uE923"   // Segoe MDL2 "Restore" icon
                : "\uE922";  // Segoe MDL2 "Maximize" icon
        }

        if (BtnMaximize != null)
        {
            BtnMaximize.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
        }
    }
}
