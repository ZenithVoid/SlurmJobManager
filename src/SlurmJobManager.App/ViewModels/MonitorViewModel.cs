using System.Collections.ObjectModel;
using System.Windows.Input;

namespace SlurmJobManager.App.ViewModels;

/// <summary>
/// ViewModel for the job monitor panel.
/// Polls squeue for a given user and exposes the job list.
/// </summary>
public sealed class MonitorViewModel : ViewModelBase
{
    private string _watchedUser = string.Empty;
    private bool _isPolling;
    private string _selectedJobInfo = string.Empty;

    public string WatchedUser
    {
        get => _watchedUser;
        set => SetField(ref _watchedUser, value);
    }

    public bool IsPolling
    {
        get => _isPolling;
        set => SetField(ref _isPolling, value);
    }

    public string SelectedJobInfo
    {
        get => _selectedJobInfo;
        set => SetField(ref _selectedJobInfo, value);
    }

    public ObservableCollection<JobRow> Jobs { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand StartPollingCommand { get; }
    public ICommand StopPollingCommand { get; }
    public ICommand CancelJobCommand { get; }

    public MonitorViewModel()
    {
        RefreshCommand = new RelayCommand(Refresh);
        StartPollingCommand = new RelayCommand(StartPolling, () => !IsPolling);
        StopPollingCommand = new RelayCommand(StopPolling, () => IsPolling);
        CancelJobCommand = new RelayCommand(CancelJob);
    }

    private void Refresh()
    {
        // TODO: call ISlurmService.GetUserJobsAsync and populate Jobs
    }

    private void StartPolling()
    {
        IsPolling = true;
        // TODO: start a periodic timer that calls Refresh
    }

    private void StopPolling()
    {
        IsPolling = false;
        // TODO: stop the timer
    }

    private void CancelJob()
    {
        // TODO: call ISlurmService.CancelJobAsync for the selected job
    }
}

/// <summary>Display row for a single Slurm job in the monitor list.</summary>
public sealed class JobRow : ViewModelBase
{
    private string _state = string.Empty;

    public long JobId { get; set; }
    public string JobName { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;

    public string State
    {
        get => _state;
        set => SetField(ref _state, value);
    }

    public string Partition { get; set; } = string.Empty;
    public string RunTime { get; set; } = string.Empty;
}
