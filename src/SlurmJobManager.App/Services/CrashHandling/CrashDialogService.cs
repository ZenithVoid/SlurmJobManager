using System.Windows;
using SlurmJobManager.App.ViewModels.Dialogs;
using SlurmJobManager.App.Views.Dialogs;

namespace SlurmJobManager.App.Services.CrashHandling;

/// <summary>
/// Shows a modal crash dialog on the UI thread, blocks until the user dismisses it,
/// and then invokes the graceful shutdown callback.
/// </summary>
internal sealed class CrashDialogService
{
    private readonly Action _shutdownAction;

    public CrashDialogService(Action shutdownAction)
    {
        _shutdownAction = shutdownAction;
    }

    /// <summary>
    /// Ensures the crash dialog is shown on the UI thread (dispatching if necessary),
    /// and blocks the calling thread until the user closes the dialog.
    /// After the dialog is dismissed the shutdown action is invoked.
    /// </summary>
    public void ShowAndWait(Exception ex, string location, string fullReport)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher == null || dispatcher.HasShutdownStarted)
        {
            // No UI available — best-effort exit
            _shutdownAction();
            return;
        }

        if (dispatcher.CheckAccess())
        {
            // Already on UI thread
            ShowDialog(ex, location, fullReport);
        }
        else
        {
            // Marshal to UI thread and wait for it to finish
            dispatcher.Invoke(() => ShowDialog(ex, location, fullReport));
        }
    }

    private void ShowDialog(Exception ex, string location, string fullReport)
    {
        try
        {
            var vm = new CrashDialogViewModel(ex, location, fullReport);
            var dialog = new CrashDialog { DataContext = vm };

            // Give the view-model a way to close its own window
            vm.SetCloseWindowAction(() => dialog.Close());

            // ShowDialog blocks until the window is closed
            dialog.ShowDialog();
        }
        catch
        {
            // If the dialog itself fails to open, skip straight to shutdown
        }
        finally
        {
            // Always trigger graceful shutdown after the dialog is dismissed (or failed to show)
            _shutdownAction();
        }
    }
}
