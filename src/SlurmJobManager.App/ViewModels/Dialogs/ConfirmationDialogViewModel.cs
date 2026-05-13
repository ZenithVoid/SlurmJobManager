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
        bool showCancelButton = true,
        string? discardButtonText = null)
    {
        Title = title;
        Message = message;
        Details = details ?? string.Empty;
        ConfirmButtonText = string.IsNullOrWhiteSpace(confirmButtonText) ? "OK" : confirmButtonText;
        CancelButtonText = string.IsNullOrWhiteSpace(cancelButtonText) ? "Cancel" : cancelButtonText;
        IsWarning = isWarning;
        ShowCancelButton = showCancelButton;
        DiscardButtonText = discardButtonText ?? string.Empty;
    }

    public string Title { get; }
    public string Message { get; }
    public string Details { get; }
    public string ConfirmButtonText { get; }
    public string CancelButtonText { get; }
    public bool IsWarning { get; }
    public bool ShowCancelButton { get; }

    /// <summary>Optional text for a neutral "discard" button shown between Cancel and Confirm.
    /// When empty, no discard button is shown. When clicked, <see cref="Views.Dialogs.ConfirmationDialogView.DiscardChosen"/>
    /// is set to <c>true</c> and the dialog closes with <c>DialogResult = true</c>.</summary>
    public string DiscardButtonText { get; }
    public bool ShowDiscardButton => !string.IsNullOrEmpty(DiscardButtonText);

    public string IconText => IsWarning ? "⚠" : "ℹ";
}
