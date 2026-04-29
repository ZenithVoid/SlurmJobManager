using System.Windows;

namespace SlurmJobManager.App.Views.Dialogs;

public partial class CrashDialog : Window
{
    public CrashDialog()
    {
        InitializeComponent();

        // Prevent close-button from dismissing the dialog until the user clicks "关闭程序"
        // so they have time to copy the stack trace.
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Once the view-model has set the close action the dialog is allowed to close normally.
        // We do not cancel the close here — closing is always initiated through CloseAppCommand.
    }
}
