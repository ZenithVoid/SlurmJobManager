namespace SlurmJobManager.App.ViewModels.Dialogs;

public sealed class ConfirmationDialogViewModel : ViewModelBase
{
    public ConfirmationDialogViewModel(
        string title,
        string message,
        string? details = null,
        string? confirmButtonText = null,
        string? cancelButtonText = null,
        bool isWarning = false,
        bool showCancelButton = true)
    {
        Title = title;
        Message = message;
        Details = details ?? string.Empty;
        ConfirmButtonText = string.IsNullOrWhiteSpace(confirmButtonText) ? "OK" : confirmButtonText;
        CancelButtonText = string.IsNullOrWhiteSpace(cancelButtonText) ? "Cancel" : cancelButtonText;
        IsWarning = isWarning;
        ShowCancelButton = showCancelButton;
    }

    public string Title { get; }
    public string Message { get; }
    public string Details { get; }
    public string ConfirmButtonText { get; }
    public string CancelButtonText { get; }
    public bool IsWarning { get; }
    public bool ShowCancelButton { get; }
    public string IconText => IsWarning ? "⚠" : "ℹ";
}
