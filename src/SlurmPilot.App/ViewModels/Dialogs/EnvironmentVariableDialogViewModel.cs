using SlurmPilot.App.ViewModels;

namespace SlurmPilot.App.ViewModels.Dialogs;

public sealed class EnvironmentVariableDialogViewModel : ViewModelBase
{
    private string _key = string.Empty;
    private string _value = string.Empty;

    public EnvironmentVariableDialogViewModel(
        string title,
        string prompt,
        string keyPlaceholder,
        string valuePlaceholder,
        string confirmButtonText,
        string cancelButtonText)
    {
        Title = title;
        Prompt = prompt;
        KeyPlaceholder = keyPlaceholder;
        ValuePlaceholder = valuePlaceholder;
        ConfirmButtonText = confirmButtonText;
        CancelButtonText = cancelButtonText;
    }

    public string Title { get; }
    public string Prompt { get; }
    public string KeyPlaceholder { get; }
    public string ValuePlaceholder { get; }
    public string ConfirmButtonText { get; }
    public string CancelButtonText { get; }
    public bool Confirmed { get; private set; }

    public string Key
    {
        get => _key;
        set
        {
            if (SetField(ref _key, value))
                OnPropertyChanged(nameof(CanConfirm));
        }
    }

    public string Value
    {
        get => _value;
        set => SetField(ref _value, value);
    }

    public bool CanConfirm => !string.IsNullOrWhiteSpace(Key);

    public void Confirm()
        => Confirmed = true;
}
