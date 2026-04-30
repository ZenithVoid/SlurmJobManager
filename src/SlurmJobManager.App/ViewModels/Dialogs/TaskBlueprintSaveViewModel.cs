using System.Windows;
using System.Windows.Input;

namespace SlurmJobManager.App.ViewModels.Dialogs;

public sealed class TaskBlueprintSaveViewModel : ViewModelBase
{
    private string _name = string.Empty;
    private string _description = string.Empty;
    private string _statusMessage = string.Empty;

    public string BlueprintName
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string BlueprintDescription
    {
        get => _description;
        set => SetField(ref _description, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public bool Confirmed { get; private set; }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    public TaskBlueprintSaveViewModel(string? initialName = null, string? initialDescription = null)
    {
        _name = initialName ?? string.Empty;
        _description = initialDescription ?? string.Empty;

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => Confirmed = false);
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(BlueprintName))
        {
            StatusMessage = L("Task.BlueprintNameRequired");
            return;
        }

        BlueprintName = BlueprintName.Trim();
        BlueprintDescription = BlueprintDescription.Trim();
        Confirmed = true;
    }

    private static string L(string key)
        => Application.Current?.TryFindResource(key) as string ?? key;
}
