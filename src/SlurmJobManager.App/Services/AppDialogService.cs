using System.Windows;
using SlurmJobManager.App.ViewModels.Dialogs;
using SlurmJobManager.App.Views.Dialogs;

namespace SlurmJobManager.App.Services;

public static class AppDialogService
{
    public static void ShowInfo(string title, string message, string? details = null, Window? owner = null)
        => ShowMessage(title, message, details, isWarning: false, owner);

    public static void ShowWarning(string title, string message, string? details = null, Window? owner = null)
        => ShowMessage(title, message, details, isWarning: true, owner);

    public static bool ConfirmWarning(
        string title,
        string message,
        string? details = null,
        string? confirmButtonText = null,
        string? cancelButtonText = null,
        Window? owner = null)
    {
        var vm = new ConfirmationDialogViewModel(
            title,
            message,
            details,
            confirmButtonText,
            cancelButtonText,
            isWarning: true);
        var dialog = CreateDialog(vm, owner);
        return dialog.ShowDialog() == true;
    }

    private static void ShowMessage(string title, string message, string? details, bool isWarning, Window? owner)
    {
        var vm = new ConfirmationDialogViewModel(
            title,
            message,
            details,
            confirmButtonText: L("Btn.Confirm", "OK"),
            cancelButtonText: null,
            isWarning: isWarning,
            showCancelButton: false);
        CreateDialog(vm, owner).ShowDialog();
    }

    private static ConfirmationDialogView CreateDialog(ConfirmationDialogViewModel vm, Window? owner)
    {
        var dialog = new ConfirmationDialogView { DataContext = vm };
        dialog.Owner = owner ?? Application.Current?.MainWindow;
        return dialog;
    }

    private static string L(string key, string fallback)
        => Application.Current?.TryFindResource(key) as string ?? fallback;
}
