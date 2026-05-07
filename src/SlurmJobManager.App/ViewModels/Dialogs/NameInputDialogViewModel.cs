namespace SlurmJobManager.App.ViewModels.Dialogs;

public sealed class NameInputDialogViewModel : ViewModelBase
{
    private string _inputValue;

    public NameInputDialogViewModel(
        string title,
        string prompt,
        string initialValue,
        string confirmButtonText,
        string cancelButtonText)
    {
        Title = title;
        Prompt = prompt;
        _inputValue = initialValue;
        ConfirmButtonText = confirmButtonText;
        CancelButtonText = cancelButtonText;
    }

    public string Title { get; }
    public string Prompt { get; }
    public string ConfirmButtonText { get; }
    public string CancelButtonText { get; }

    public string InputValue
    {
        get => _inputValue;
        set => SetField(ref _inputValue, value);
    }

    public bool Confirmed { get; private set; }

    public void Confirm()
        => Confirmed = true;
}
