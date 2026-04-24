namespace SlurmJobManager.App.ViewModels;

/// <summary>Root view-model: owns all child VMs and a status message.</summary>
public sealed class MainViewModel : ViewModelBase
{
    private string _statusMessage = "Ready";

    public ConnectionViewModel Connection { get; }
    public TaskEditorViewModel TaskEditor { get; }
    public MonitorViewModel    Monitor    { get; }
    public LogViewerViewModel  LogViewer  { get; }
    public ConsoleViewModel    Console    { get; }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public MainViewModel(
        ConnectionViewModel connection,
        TaskEditorViewModel taskEditor,
        MonitorViewModel    monitor,
        LogViewerViewModel  logViewer,
        ConsoleViewModel    console)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        TaskEditor = taskEditor ?? throw new ArgumentNullException(nameof(taskEditor));
        Monitor    = monitor    ?? throw new ArgumentNullException(nameof(monitor));
        LogViewer  = logViewer  ?? throw new ArgumentNullException(nameof(logViewer));
        Console    = console    ?? throw new ArgumentNullException(nameof(console));
    }
}
