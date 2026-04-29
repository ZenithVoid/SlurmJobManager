using System.Windows;
using System.Windows.Controls;
using SlurmJobManager.App.ViewModels;
using SlurmJobManager.App.ViewModels.Dialogs;

namespace SlurmJobManager.App.Views.Dialogs;

/// <summary>Code-behind for the Command Builder dialog.</summary>
public partial class CommandBuilderView : Window
{
    public CommandBuilderView()
    {
        InitializeComponent();
    }

    // ── Chosen param-file list: track selection so Remove command works ────

    private void ChosenParamList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is ListBox lb && lb.SelectedItem is ParameterFileEntryViewModel selected
            && DataContext is CommandBuilderViewModel vm)
        {
            vm.SelectedChosenParamFile = selected;
            vm.EditParamFileCommand.Execute(selected);
        }
    }

    // ── Confirm button ────────────────────────────────────────────────────

    private void BtnConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CommandBuilderViewModel vm)
        {
            vm.ConfirmCommand.Execute(null);
            DialogResult = true;
        }
    }

    // ── Cancel button ──────────────────────────────────────────────────────

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}
