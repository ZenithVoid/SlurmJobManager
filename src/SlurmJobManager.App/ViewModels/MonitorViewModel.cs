using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Models;

namespace SlurmJobManager.App.ViewModels;

/// <summary>
/// Polls squeue for a given user and exposes the job list.
/// </summary>
public sealed class MonitorViewModel : ViewModelBase
{
    private readonly ISlurmService _slurm;
    private DispatcherTimer? _timer;

    private string _watchedUser = string.Empty;
    private int _pollIntervalSeconds = 3;
    private bool _isPolling;
    private string _statusMessage = string.Empty;
    private JobRow? _selectedJob;

    public string WatchedUser      { get => _watchedUser;         set => SetField(ref _watchedUser, value); }
    public int PollIntervalSeconds { get => _pollIntervalSeconds; set { SetField(ref _pollIntervalSeconds, value); UpdateTimerInterval(); } }
    public bool IsPolling          { get => _isPolling;           private set => SetField(ref _isPolling, value); }
    public string StatusMessage    { get => _statusMessage;       set => SetField(ref _statusMessage, value); }
    public JobRow? SelectedJob     { get => _selectedJob;         set => SetField(ref _selectedJob, value); }

    public ObservableCollection<JobRow> Jobs { get; } = new();

    public ICommand RefreshCommand      { get; }
    public ICommand StartPollingCommand { get; }
    public ICommand StopPollingCommand  { get; }
    public ICommand CancelJobCommand    { get; }

    public MonitorViewModel(ISlurmService slurm)
    {
        _slurm = slurm ?? throw new ArgumentNullException(nameof(slurm));

        RefreshCommand      = new AsyncRelayCommand(RefreshAsync);
        StartPollingCommand = new RelayCommand(StartPolling, () => !IsPolling);
        StopPollingCommand  = new RelayCommand(StopPolling,  () => IsPolling);
        CancelJobCommand    = new AsyncRelayCommand(CancelSelectedJobAsync, () => SelectedJob != null);
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(WatchedUser))
        {
            StatusMessage = "Enter a username to watch.";
            return;
        }

        StatusMessage = "Refreshing…";
        try
        {
            var jobs = await _slurm.GetUserJobsAsync(WatchedUser, ct);
            Application.Current.Dispatcher.Invoke(() =>
            {
                Jobs.Clear();
                foreach (var j in jobs)
                    Jobs.Add(new JobRow
                    {
                        JobId     = j.JobId,
                        JobName   = j.JobName,
                        User      = j.User,
                        State     = j.State,
                        Partition = j.Partition,
                        RunTime   = j.RunTime?.ToString(@"hh\:mm\:ss") ?? string.Empty,
                        NodeList  = j.NodeList ?? string.Empty,
                    });
            });
            StatusMessage = $"Updated: {DateTime.Now:HH:mm:ss}  ({jobs.Count} job(s))";
        }
        catch (Exception ex) { StatusMessage = $"Refresh failed: {ex.Message}"; }
    }

    private void StartPolling()
    {
        if (IsPolling) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_pollIntervalSeconds) };
        _timer.Tick += async (_, _) => await RefreshAsync(CancellationToken.None);
        _timer.Start();
        IsPolling = true;
        StatusMessage = $"Polling every {_pollIntervalSeconds}s…";
    }

    private void StopPolling()
    {
        _timer?.Stop();
        _timer = null;
        IsPolling = false;
        StatusMessage = "Polling stopped.";
    }

    private async Task CancelSelectedJobAsync(CancellationToken ct)
    {
        if (SelectedJob == null) return;
        StatusMessage = $"Cancelling job {SelectedJob.JobId}…";
        try
        {
            await _slurm.CancelJobAsync(SelectedJob.JobId, ct);
            StatusMessage = $"Job {SelectedJob.JobId} cancelled.";
            await RefreshAsync(ct);
        }
        catch (Exception ex) { StatusMessage = $"Cancel failed: {ex.Message}"; }
    }

    private void UpdateTimerInterval()
    {
        if (_timer != null)
            _timer.Interval = TimeSpan.FromSeconds(_pollIntervalSeconds);
    }
}

/// <summary>Display row for a single Slurm job in the monitor list.</summary>
public sealed class JobRow : ViewModelBase
{
    private string _state = string.Empty;

    public long JobId     { get; set; }
    public string JobName { get; set; } = string.Empty;
    public string User    { get; set; } = string.Empty;
    public string State   { get => _state; set => SetField(ref _state, value); }
    public string Partition { get; set; } = string.Empty;
    public string RunTime   { get; set; } = string.Empty;
    public string NodeList  { get; set; } = string.Empty;
}
