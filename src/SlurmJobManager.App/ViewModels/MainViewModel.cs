namespace SlurmJobManager.App.ViewModels;

/// <summary>Root view-model: owns the three child VMs and a status message.</summary>
public sealed class MainViewModel : ViewModelBase
{
    private string _statusMessage = "Ready";

    public TaskEditorViewModel TaskEditor { get; } = new();
    public MonitorViewModel Monitor { get; } = new();
    public LogViewerViewModel LogViewer { get; } = new();

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }
}
