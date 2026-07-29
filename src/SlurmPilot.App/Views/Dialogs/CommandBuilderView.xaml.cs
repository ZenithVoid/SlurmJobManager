using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SlurmPilot.App.ViewModels.Dialogs;

namespace SlurmPilot.App.Views.Dialogs;

/// <summary>Code-behind for the upgraded Command Builder dialog.</summary>
public partial class CommandBuilderView : Window
{
    public CommandBuilderView()
    {
        InitializeComponent();
        StateChanged += CommandBuilderView_StateChanged;
    }

    // ── Custom title-bar interactions ─────────────────────────────────────

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
        }
        else if (WindowState == WindowState.Normal || WindowState == WindowState.Maximized)
        {
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
            DragMove();
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        => ToggleMaximize();

    private void BtnClose_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CommandBuilderView_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeIcon != null)
        {
            MaximizeIcon.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        }
        if (BtnMaximize != null)
        {
            var key = WindowState == WindowState.Maximized ? "TitleBar.Restore" : "TitleBar.Maximize";
            BtnMaximize.ToolTip = Application.Current?.TryFindResource(key) as string
                                   ?? (WindowState == WindowState.Maximized ? "Restore" : "Maximize");
        }
    }

    // ── Program list double-click: assign to selected command ─────────────

    private void ProgramList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is CommandBuilderViewModel vm
            && vm.ApplySelectedProgramCommand.CanExecute(null))
        {
            vm.ApplySelectedProgramCommand.Execute(null);
        }
    }

    // ── Chosen param-file list double-click: open remote editor ──────────

    private void ChosenParamList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox lb && lb.SelectedItem is string path
            && DataContext is CommandBuilderViewModel vm)
        {
            vm.EditParamFileCommand.Execute(path);
        }
    }

    // ── Regenerate sbatch ─────────────────────────────────────────────────

    private void BtnRegenSbatch_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CommandBuilderViewModel vm)
            vm.RegenerateSbatch();
    }

    // ── Save & Apply button ───────────────────────────────────────────────

    private void BtnSaveApply_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CommandBuilderViewModel vm)
        {
            vm.SaveAndApplyCommand.Execute(null);
            if (vm.Confirmed)
                DialogResult = true;
        }
    }

    // ── Cancel button ──────────────────────────────────────────────────────

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}
