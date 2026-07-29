using System.Collections.Specialized;
using System.Windows.Input;

namespace SlurmPilot.App.ViewModels;

/// <summary>
/// Dashboard overview: connection status, job statistics, and quick-action shortcuts.
/// </summary>
public sealed class DashboardViewModel : ViewModelBase
{
    private readonly ConnectionViewModel _connection;
    private readonly MonitorViewModel    _monitor;

    public DashboardViewModel(
        ConnectionViewModel connection,
        MonitorViewModel    monitor,
        Action<string>      navigate)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _monitor    = monitor    ?? throw new ArgumentNullException(nameof(monitor));

        NavigateToTasksCommand    = new RelayCommand(() => navigate("Tasks"));
        NavigateToMonitorCommand  = new RelayCommand(() => navigate("Monitor"));
        NavigateToLogsCommand     = new RelayCommand(() => navigate("Logs"));
        NavigateToSettingsCommand = new RelayCommand(() => navigate("Settings"));
        QuickTestCommand         = _connection.TestConnectionCommand;
        QuickRefreshCommand      = _monitor.RefreshCommand;

        _connection.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ConnectionStatusText));
            OnPropertyChanged(nameof(ConnectionStatusMessage));
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(IsEmpty));
        };

        _monitor.Jobs.CollectionChanged += OnJobsChanged;
        _monitor.PropertyChanged        += (_, e) =>
        {
            if (e.PropertyName == nameof(MonitorViewModel.StatusMessage))
                OnPropertyChanged(nameof(MonitorStatusMessage));
        };
    }

    // ── Connection status ─────────────────────────────────────────────────

    public string ConnectionStatusText    => _connection.StatusText;
    public string ConnectionStatusMessage => _connection.StatusMessage;
    public bool   IsConnected             => _connection.IsConnected;

    /// <summary>True when there is no active SSH connection (drives the empty-state overlay).</summary>
    public bool IsEmpty => !IsConnected;

    // ── Job statistics ────────────────────────────────────────────────────

    public int TotalJobs     => _monitor.Jobs.Count;
    public int RunningJobs   => _monitor.Jobs.Count(j => j.State is "RUNNING" or "COMPLETING");
    public int PendingJobs   => _monitor.Jobs.Count(j => j.State == "PENDING");
    public int FailedJobs    => _monitor.Jobs.Count(j => j.State is "FAILED" or "NODE_FAIL");
    public int CompletedJobs => _monitor.Jobs.Count(j => j.State == "COMPLETED");

    public string MonitorStatusMessage => _monitor.StatusMessage;

    // ── Navigation commands ───────────────────────────────────────────────

    public ICommand NavigateToTasksCommand    { get; }
    public ICommand NavigateToMonitorCommand  { get; }
    public ICommand NavigateToLogsCommand     { get; }
    public ICommand NavigateToSettingsCommand { get; }

    // ── Quick-action commands ─────────────────────────────────────────────

    public ICommand QuickTestCommand    { get; }
    public ICommand QuickRefreshCommand { get; }

    // ── Private helpers ───────────────────────────────────────────────────

    private void OnJobsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(TotalJobs));
        OnPropertyChanged(nameof(RunningJobs));
        OnPropertyChanged(nameof(PendingJobs));
        OnPropertyChanged(nameof(FailedJobs));
        OnPropertyChanged(nameof(CompletedJobs));
    }
}
