using System.Windows;
using System.Windows.Input;

namespace SlurmPilot.App.ViewModels.Dialogs;

/// <summary>
/// View-model for the fatal-error crash dialog.
/// Exposes all diagnostic fields and commands needed by <see cref="Views.Dialogs.CrashDialog"/>.
/// </summary>
internal sealed class CrashDialogViewModel : ViewModelBase
{
    private Action? _closeWindowAction;

    // ── Diagnostic fields ────────────────────────────────────────────────

    /// <summary>Fully-qualified exception type name.</summary>
    public string ExceptionType { get; }

    /// <summary>Exception message.</summary>
    public string Message { get; }

    /// <summary>Best-effort location: first SlurmPilot frame in the stack trace.</summary>
    public string Location { get; }

    /// <summary>Full stack trace text (may include inner exceptions).</summary>
    public string StackTrace { get; }

    /// <summary>Timestamp when the dialog was created (i.e., when the crash was caught).</summary>
    public string OccurredAt { get; }

    /// <summary>Complete diagnostic report that is copied to clipboard.</summary>
    private readonly string _fullReport;

    // ── Commands ─────────────────────────────────────────────────────────

    /// <summary>Copies the full diagnostic report to the system clipboard.</summary>
    public ICommand CopyDetailsCommand { get; }

    /// <summary>Closes the dialog window (shutdown is triggered by CrashDialogService).</summary>
    public ICommand CloseAppCommand { get; }

    public CrashDialogViewModel(Exception ex, string location, string fullReport)
    {
        ExceptionType = ex.GetType().FullName ?? ex.GetType().Name;
        Message       = ex.Message;
        Location      = location;
        StackTrace    = BuildStackText(ex);
        OccurredAt    = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _fullReport   = fullReport;

        CopyDetailsCommand = new RelayCommand(CopyDetails);
        CloseAppCommand    = new RelayCommand(CloseApp);
    }

    /// <summary>
    /// Called by the view (code-behind) so the view-model can close its own window
    /// without holding a direct reference to the Window.
    /// </summary>
    public void SetCloseWindowAction(Action action) => _closeWindowAction = action;

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string BuildStackText(Exception ex)
    {
        var parts = new System.Text.StringBuilder();
        var current = ex;
        var depth = 0;

        while (current != null)
        {
            if (depth > 0)
                parts.AppendLine($"\n--- 内部异常 ({current.GetType().Name}) ---");

            parts.AppendLine(current.StackTrace ?? "(无堆栈信息)");
            current = current.InnerException;
            depth++;
        }

        return parts.ToString().TrimEnd();
    }

    private void CopyDetails()
    {
        try
        {
            Clipboard.SetText(_fullReport);
        }
        catch
        {
            // Best-effort: clipboard may be locked by another process
        }
    }

    private void CloseApp()
    {
        // Close the window first; CrashDialogService will trigger shutdown after ShowDialog returns.
        _closeWindowAction?.Invoke();
    }
}
