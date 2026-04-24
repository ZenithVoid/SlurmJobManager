using System.Collections.ObjectModel;
using System.Windows.Input;

namespace SlurmJobManager.App.ViewModels;

/// <summary>
/// ViewModel for the task editor panel.
/// Manages task root directory, task ID, parameter key/value pairs
/// and the selected sbatch template.
/// </summary>
public sealed class TaskEditorViewModel : ViewModelBase
{
    private string _rootDirectory = string.Empty;
    private string _taskId = string.Empty;
    private string? _templateFileName;
    private bool _isBusy;

    public string RootDirectory
    {
        get => _rootDirectory;
        set => SetField(ref _rootDirectory, value);
    }

    public string TaskId
    {
        get => _taskId;
        set => SetField(ref _taskId, value);
    }

    public string? TemplateFileName
    {
        get => _templateFileName;
        set => SetField(ref _templateFileName, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetField(ref _isBusy, value);
    }

    /// <summary>Editable parameter key/value pairs shown in a data grid.</summary>
    public ObservableCollection<ParameterEntry> Parameters { get; } = new();

    public ICommand BrowseRootDirectoryCommand { get; }
    public ICommand LoadTemplateCommand { get; }
    public ICommand SaveTaskCommand { get; }
    public ICommand SubmitJobCommand { get; }

    public TaskEditorViewModel()
    {
        BrowseRootDirectoryCommand = new RelayCommand(BrowseRootDirectory);
        LoadTemplateCommand = new RelayCommand(LoadTemplate);
        SaveTaskCommand = new RelayCommand(SaveTask);
        SubmitJobCommand = new RelayCommand(SubmitJob, () => !IsBusy);
    }

    private void BrowseRootDirectory()
    {
        // TODO: open FolderBrowserDialog and set RootDirectory
    }

    private void LoadTemplate()
    {
        // TODO: open OpenFileDialog, read template, populate Parameters
    }

    private void SaveTask()
    {
        // TODO: persist via ITaskStorageService
    }

    private void SubmitJob()
    {
        // TODO: render template + call ISlurmService.SubmitSbatchAsync
    }
}

/// <summary>A single editable parameter entry.</summary>
public sealed class ParameterEntry : ViewModelBase
{
    private string _key = string.Empty;
    private string _value = string.Empty;

    public string Key
    {
        get => _key;
        set => SetField(ref _key, value);
    }

    public string Value
    {
        get => _value;
        set => SetField(ref _value, value);
    }
}
