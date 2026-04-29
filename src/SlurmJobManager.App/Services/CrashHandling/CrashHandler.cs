using SlurmJobManager.Core.Interfaces;

namespace SlurmJobManager.App.Services.CrashHandling;

/// <summary>
/// Handles unhandled exceptions: logs the full diagnostic report and displays a crash dialog.
/// Prevents re-entrancy so a crash in the crash handler does not recurse infinitely.
/// </summary>
internal sealed class CrashHandler
{
    private readonly IAppLogger?       _logger;
    private readonly CrashDialogService _dialogService;

    // Volatile flag guards against re-entrant crash handling (e.g., crash inside crash dialog).
    private static volatile bool _isHandling;

    public CrashHandler(IAppLogger? logger, CrashDialogService dialogService)
    {
        _logger        = logger;
        _dialogService = dialogService;
    }

    /// <summary>
    /// Logs the exception, shows the crash dialog, and then triggers graceful shutdown.
    /// Safe to call from any thread.
    /// </summary>
    /// <param name="ex">The unhandled exception.</param>
    /// <param name="source">A short label identifying which hook caught the exception.</param>
    public void HandleException(Exception ex, string source)
    {
        if (_isHandling) return;
        _isHandling = true;

        try
        {
            var report   = CrashReportBuilder.BuildReport(ex);
            var location = CrashReportBuilder.ExtractLocation(ex);

            // Write to log first (best-effort — logging must not block crash dialog)
            try
            {
                _logger?.Error(
                    $"[FATAL/{source}] Unhandled exception at {location}\n{report}", ex);
            }
            catch
            {
                // If logging itself fails, continue to show the dialog
            }

            // Show crash dialog and wait for user acknowledgement, then shut down
            _dialogService.ShowAndWait(ex, location, report);
        }
        finally
        {
            _isHandling = false;
        }
    }
}
