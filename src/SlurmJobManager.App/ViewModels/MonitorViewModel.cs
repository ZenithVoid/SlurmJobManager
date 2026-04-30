using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Models;
using SlurmJobManager.Infrastructure.Resilience;

namespace SlurmJobManager.App.ViewModels;

/// <summary>
/// Polls squeue for active jobs and queries sacct for historical jobs.
/// Supports filtering, keyword search, row actions, and reconnect-safe polling.
/// </summary>
public sealed class MonitorViewModel : ViewModelBase, IDisposable
{
    private const int HistoryFetchLimit = 200;

    private readonly ISlurmService _slurm;
    private readonly AppSettings _settings;
    private readonly IAppLogger? _logger;
    private readonly ConnectionViewModel? _connection;

    // Polling infrastructure
    private DispatcherTimer? _pollTimer;
    private readonly DispatcherTimer _elapsedTimer;
    private int _consecutiveFailures;
    private bool _isRefreshing;

    // Source-of-truth caches
    private List<JobRow> _allCurrentJobs = new();

    private string _watchedUser = string.Empty;
    private int _pollIntervalSeconds = 3;
    private bool _isPolling;
    private bool _showAllUsers;
    private string _statusMessage = string.Empty;
    private string _statusStyleKey = "InfoTextStyle";
    private JobRow? _selectedCurrentJob;
    private JobRow? _selectedHistoryJob;
    private string _allStatusFilter = string.Empty;
    private string _statusFilter = string.Empty;
    private string _searchText = string.Empty;

    public string WatchedUser
    {
        get => _watchedUser;
        set
        {
            if (SetField(ref _watchedUser, value))
            {
                OnPropertyChanged(nameof(IsEmptyState));
                OnPropertyChanged(nameof(CanQueryHistory));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public int PollIntervalSeconds
    {
        get => _pollIntervalSeconds;
        set
        {
            if (SetField(ref _pollIntervalSeconds, value))
                UpdatePollTimerInterval();
        }
    }

    public bool IsPolling
    {
        get => _isPolling;
        private set
        {
            if (SetField(ref _isPolling, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public string StatusStyleKey
    {
        get => _statusStyleKey;
        private set => SetField(ref _statusStyleKey, value);
    }

    public JobRow? SelectedCurrentJob
    {
        get => _selectedCurrentJob;
        set
        {
            if (SetField(ref _selectedCurrentJob, value))
            {
                OnPropertyChanged(nameof(SelectedJob));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>Backward-compatible selected job alias for existing consumers.</summary>
    public JobRow? SelectedJob
    {
        get => SelectedCurrentJob;
        set => SelectedCurrentJob = value;
    }

    public JobRow? SelectedHistoryJob
    {
        get => _selectedHistoryJob;
        set => SetField(ref _selectedHistoryJob, value);
    }

    /// <summary>When true, squeue is queried without a user filter.</summary>
    public bool ShowAllUsers
    {
        get => _showAllUsers;
        set
        {
            if (SetField(ref _showAllUsers, value))
            {
                OnPropertyChanged(nameof(IsEmptyState));
                OnPropertyChanged(nameof(CanQueryHistory));
                CommandManager.InvalidateRequerySuggested();
                RefreshCommand.Execute(null);
            }
        }
    }

    /// <summary>True when no watched user has been configured yet.</summary>
    public bool IsEmptyState => !ShowAllUsers && string.IsNullOrWhiteSpace(WatchedUser);

    /// <summary>True when a concrete username is available for history lookup.</summary>
    public bool CanQueryHistory => !string.IsNullOrWhiteSpace(GetHistoryQueryUser());

    public string StatusFilter
    {
        get => _statusFilter;
        set
        {
            if (SetField(ref _statusFilter, value))
                ApplyCurrentFilter();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetField(ref _searchText, value))
                ApplyCurrentFilter();
        }
    }

    /// <summary>Available status filter options for current jobs.</summary>
    public ObservableCollection<string> StatusFilterOptions { get; } = new();

    /// <summary>Filtered active jobs displayed in the current-jobs table.</summary>
    public ObservableCollection<JobRow> CurrentJobs { get; } = new();

    /// <summary>Historical jobs displayed in the history table.</summary>
    public ObservableCollection<JobRow> HistoryJobs { get; } = new();

    /// <summary>Backward-compatible alias consumed by Dashboard.</summary>
    public ObservableCollection<JobRow> Jobs => CurrentJobs;

    // Legacy command names retained for compatibility.
    public ICommand RefreshCommand { get; }
    public ICommand StartPollingCommand { get; }
    public ICommand StopPollingCommand { get; }
    public ICommand CancelJobCommand { get; }

    // New split-view commands.
    public ICommand RefreshCurrentJobsCommand { get; }
    public ICommand RefreshHistoryJobsCommand { get; }
    public ICommand CancelCurrentJobCommand { get; }
    public ICommand ViewHistoryDetailCommand { get; }

    public MonitorViewModel(
        ISlurmService slurm,
        AppSettings? settings = null,
        IAppLogger? logger = null,
        ConnectionViewModel? connection = null)
    {
        _slurm = slurm ?? throw new ArgumentNullException(nameof(slurm));
        _settings = settings ?? new AppSettings();
        _logger = logger;
        _connection = connection;

        ResetFilterOptions();
        _statusFilter = _allStatusFilter;

        RefreshCommand = new AsyncRelayCommand(RefreshCurrentJobsAsync);
        RefreshCurrentJobsCommand = RefreshCommand;
        RefreshHistoryJobsCommand = new AsyncRelayCommand(RefreshHistoryJobsAsync, () => CanQueryHistory);
        StartPollingCommand = new RelayCommand(StartPolling, () => !IsPolling);
        StopPollingCommand = new RelayCommand(StopPolling, () => IsPolling);
        CancelJobCommand = new AsyncRelayCommand(ct => CancelJobAsync(SelectedCurrentJob, ct), () => SelectedCurrentJob != null);
        CancelCurrentJobCommand = new AsyncRelayCommand<JobRow>((row, ct) => CancelJobAsync(row, ct), row => row is not null);
        ViewHistoryDetailCommand = new RelayCommand<JobRow>(ShowHistoryDetail, row => row is not null);

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += OnElapsedTick;
        _elapsedTimer.Start();

        if (_connection != null)
        {
            _connection.PropertyChanged += OnConnectionPropertyChanged;
            if (_connection.IsConnected && !string.IsNullOrWhiteSpace(_connection.Username) && string.IsNullOrWhiteSpace(WatchedUser))
                WatchedUser = _connection.Username;
        }

        SetStatus("Monitor.EmptyState", "InfoTextStyle");
    }

    // ── Current jobs refresh ───────────────────────────────────────────────

    private async Task RefreshCurrentJobsAsync(CancellationToken ct)
    {
        if (!ShowAllUsers && string.IsNullOrWhiteSpace(WatchedUser))
        {
            SetStatus("Monitor.EmptyState", "InfoTextStyle");
            return;
        }

        SetStatus("Monitor.CurrentRefreshing", "InfoTextStyle");
        try
        {
            IReadOnlyList<SlurmJobStatus> jobs = ShowAllUsers
                ? await _slurm.GetAllJobsAsync(ct)
                : await _slurm.GetUserJobsAsync(WatchedUser, ct);

            Application.Current.Dispatcher.Invoke(() =>
            {
                _allCurrentJobs = jobs
                    .Select(MapCurrentJob)
                    .OrderByDescending(j => j.JobId)
                    .ToList();
                ApplyCurrentFilter();
                UpdateCurrentElapsedDisplays();
            });

            _consecutiveFailures = 0;
            SetStatus(string.Format(L("Monitor.CurrentUpdated"), DateTime.Now, jobs.Count), "SuccessTextStyle", localize: false);
            _logger?.Debug($"Monitor current jobs refreshed: {jobs.Count} job(s)");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            var msg = ConnectionViewModel.ClassifyError(ex);
            SetStatus(string.Format(L("Monitor.CurrentRefreshFailed"), msg), "ErrorTextStyle", localize: false);
            _logger?.Warning($"Current job refresh failed ({_consecutiveFailures}× consecutive): {ex.Message}");
        }
    }

    // ── History refresh ────────────────────────────────────────────────────

    private async Task RefreshHistoryJobsAsync(CancellationToken ct)
    {
        var historyUser = GetHistoryQueryUser();
        if (string.IsNullOrWhiteSpace(historyUser))
        {
            SetStatus("Monitor.HistoryNeedUser", "WarningTextStyle");
            return;
        }

        SetStatus(string.Format(L("Monitor.HistoryRefreshing"), historyUser), "InfoTextStyle", localize: false);
        try
        {
            var jobs = await _slurm.GetUserJobHistoryAsync(historyUser, HistoryFetchLimit, ct);
            Application.Current.Dispatcher.Invoke(() =>
            {
                HistoryJobs.Clear();
                foreach (var row in jobs.Select(MapHistoricalJob).OrderByDescending(j => j.StartTime ?? DateTime.MinValue))
                    HistoryJobs.Add(row);
            });

            SetStatus(string.Format(L("Monitor.HistoryUpdated"), DateTime.Now, jobs.Count, historyUser), "SuccessTextStyle", localize: false);
            _logger?.Debug($"Monitor history refreshed: {jobs.Count} job(s) for '{historyUser}'");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var msg = ConnectionViewModel.ClassifyError(ex);
            SetStatus(string.Format(L("Monitor.HistoryRefreshFailed"), msg), "ErrorTextStyle", localize: false);
            _logger?.Warning($"History refresh failed: {ex.Message}");
        }
    }

    // ── Polling (current jobs only) ────────────────────────────────────────

    private void StartPolling()
    {
        if (IsPolling) return;
        _consecutiveFailures = 0;

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_pollIntervalSeconds) };
        _pollTimer.Tick += OnPollTick;
        _pollTimer.Start();

        IsPolling = true;
        SetStatus(string.Format(L("Monitor.PollingEvery"), _pollIntervalSeconds), "InfoTextStyle", localize: false);
        _logger?.Info($"Monitor polling started for user '{WatchedUser}'");
    }

    private void StopPolling()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
        IsPolling = false;
        SetStatus("Monitor.PollingStopped", "InfoTextStyle");
        _logger?.Info("Monitor polling stopped.");
    }

    private async void OnPollTick(object? sender, EventArgs e)
    {
        if (_isRefreshing) return;
        _isRefreshing = true;

        try
        {
            if (_consecutiveFailures >= _settings.MaxReconnectAttempts)
            {
                StopPolling();
                SetStatus(string.Format(L("Monitor.PollingHalted"), _consecutiveFailures), "ErrorTextStyle", localize: false);
                _logger?.Error($"Polling halted: {_consecutiveFailures} consecutive failures.");
                if (_connection is not null)
                    _connection.Status = ConnectionStatus.Error;
                return;
            }

            if (_connection is not null && !_connection.IsConnected)
            {
                _connection.Status = ConnectionStatus.Reconnecting;
                _logger?.Info("SSH disconnected — attempting reconnect…");
                using var reconnectCts = new CancellationTokenSource(_settings.ConnectionTimeout);
                var reconnected = await _connection.TryReconnectAsync(reconnectCts.Token);
                if (!reconnected)
                {
                    _consecutiveFailures++;
                    SetStatus(string.Format(L("Monitor.ReconnectFailed"), _consecutiveFailures, _settings.MaxReconnectAttempts), "WarningTextStyle", localize: false);
                    _logger?.Warning($"Reconnect attempt failed ({_consecutiveFailures}).");
                    return;
                }

                _logger?.Info("Reconnected successfully — resuming polling.");
            }

            await RefreshCurrentJobsAsync(CancellationToken.None);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void OnElapsedTick(object? sender, EventArgs e)
        => UpdateCurrentElapsedDisplays();

    private void UpdateCurrentElapsedDisplays()
    {
        var now = DateTime.Now;
        foreach (var row in _allCurrentJobs)
        {
            if (row.State is SlurmJobState.Running or SlurmJobState.Completing)
                row.RefreshDisplays(now);
        }
    }

    private void ApplyCurrentFilter()
    {
        var filtered = _allCurrentJobs.AsEnumerable();

        if (StatusFilter != _allStatusFilter)
            filtered = filtered.Where(j => j.State.Equals(StatusFilter, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = SearchText.Trim();
            if (long.TryParse(q, out var jobIdQuery))
            {
                filtered = filtered.Where(j =>
                    j.JobId == jobIdQuery ||
                    j.JobName.Contains(q, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                filtered = filtered.Where(j =>
                    j.JobName.Contains(q, StringComparison.OrdinalIgnoreCase));
            }
        }

        CurrentJobs.Clear();
        foreach (var row in filtered)
            CurrentJobs.Add(row);

        if (SelectedCurrentJob is not null && !CurrentJobs.Contains(SelectedCurrentJob))
            SelectedCurrentJob = null;
    }

    private async Task CancelJobAsync(JobRow? job, CancellationToken ct)
    {
        if (job is null)
        {
            SetStatus("Err.NoJobSelected", "WarningTextStyle");
            return;
        }

        var jobId = job.JobId;
        var confirmTemplate = L("Monitor.CancelConfirm");
        var confirmTitle = L("Monitor.CancelConfirmTitle");
        var confirm = MessageBox.Show(
            string.Format(confirmTemplate, jobId, job.JobName),
            confirmTitle,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
        {
            SetStatus("Monitor.CancelAborted", "InfoTextStyle");
            return;
        }

        SetStatus(string.Format(L("Monitor.Cancelling"), jobId), "InfoTextStyle", localize: false);
        try
        {
            await _slurm.CancelJobAsync(jobId, ct);
            SetStatus(string.Format(L("Monitor.CancelSucceeded"), jobId), "SuccessTextStyle", localize: false);
            _logger?.Info($"Job {jobId} cancelled by user.");
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("Invalid job id", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("already", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus(string.Format(L("Monitor.CancelStale"), jobId), "WarningTextStyle", localize: false);
            }
            else
            {
                SetStatus(string.Format(L("Monitor.CancelFailed"), jobId, ex.Message), "ErrorTextStyle", localize: false);
            }
        }

        await RefreshCurrentJobsAsync(ct);
    }

    private void ShowHistoryDetail(JobRow? row)
    {
        if (row is null) return;

        var details = string.Join(Environment.NewLine, new[]
        {
            $"{L("Monitor.ColJobId")}: {row.JobId}",
            $"{L("Monitor.ColName")}: {ValueOrDash(row.JobName)}",
            $"{L("Monitor.ColState")}: {ValueOrDash(row.State)}",
            $"{L("Monitor.DetailExitCode")}: {ValueOrDash(row.ExitCode)}",
            $"{L("Monitor.DetailReason")}: {ValueOrDash(row.Reason)}",
            $"{L("Monitor.ColPartition")}: {ValueOrDash(row.Partition)}",
            $"{L("Monitor.ColNodes")}: {ValueOrDash(row.NodeList)}",
            $"{L("Monitor.ColStartTime")}: {ValueOrDash(row.StartTimeDisplay)}",
            $"{L("Monitor.ColEndTime")}: {ValueOrDash(row.EndTimeDisplay)}",
            $"{L("Monitor.ColRunTime")}: {ValueOrDash(row.RunTimeDisplay)}",
        });

        MessageBox.Show(details, L("Monitor.HistoryDetailTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private JobRow MapCurrentJob(SlurmJobStatus status)
    {
        var row = new JobRow
        {
            JobId = status.JobId,
            JobName = status.JobName,
            User = status.User,
            State = status.State,
            Partition = status.Partition,
            NodeList = status.NodeList ?? string.Empty,
            StartTime = status.StartTime,
            EndTime = status.EndTime,
            Elapsed = status.RunTime,
            ExitCode = status.ExitCode,
            Reason = status.Reason,
            IsHistorical = false,
        };
        row.RefreshDisplays(DateTime.Now);
        return row;
    }

    private JobRow MapHistoricalJob(SlurmJobStatus status)
    {
        var row = new JobRow
        {
            JobId = status.JobId,
            JobName = status.JobName,
            User = status.User,
            State = status.State,
            Partition = status.Partition,
            NodeList = status.NodeList ?? string.Empty,
            StartTime = status.StartTime,
            EndTime = status.EndTime,
            Elapsed = status.RunTime,
            ExitCode = status.ExitCode,
            Reason = status.Reason,
            IsHistorical = true,
        };
        row.RefreshDisplays(DateTime.Now);
        return row;
    }

    private static string ValueOrDash(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private string GetHistoryQueryUser()
    {
        if (!string.IsNullOrWhiteSpace(WatchedUser))
            return WatchedUser.Trim();
        if (!string.IsNullOrWhiteSpace(_connection?.Username))
            return _connection.Username.Trim();
        return string.Empty;
    }

    private static string L(string key)
        => Application.Current?.TryFindResource(key) as string ?? key;

    internal void NotifyLocaleChanged()
    {
        var previousAll = _allStatusFilter;
        var wasAll = string.Equals(StatusFilter, previousAll, StringComparison.Ordinal);
        ResetFilterOptions();
        if (wasAll)
            StatusFilter = _allStatusFilter;
        OnPropertyChanged(nameof(StatusFilterOptions));
        OnPropertyChanged(nameof(StatusMessage));
    }

    private void ResetFilterOptions()
    {
        _allStatusFilter = L("Monitor.FilterAll");
        StatusFilterOptions.Clear();
        StatusFilterOptions.Add(_allStatusFilter);
        StatusFilterOptions.Add(SlurmJobState.Pending);
        StatusFilterOptions.Add(SlurmJobState.Running);
        StatusFilterOptions.Add(SlurmJobState.Completed);
        StatusFilterOptions.Add(SlurmJobState.Failed);
        StatusFilterOptions.Add(SlurmJobState.Cancelled);
        StatusFilterOptions.Add(SlurmJobState.Timeout);
    }

    private void SetStatus(string messageOrKey, string styleKey, bool localize = true)
    {
        StatusStyleKey = styleKey;
        StatusMessage = localize ? L(messageOrKey) : messageOrKey;
    }

    private void UpdatePollTimerInterval()
    {
        if (_pollTimer != null)
            _pollTimer.Interval = TimeSpan.FromSeconds(_pollIntervalSeconds);
    }

    private void OnConnectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConnectionViewModel.Username))
        {
            OnPropertyChanged(nameof(CanQueryHistory));
            CommandManager.InvalidateRequerySuggested();
            return;
        }

        if (e.PropertyName is not nameof(ConnectionViewModel.IsConnected)) return;
        if (_connection is null || !_connection.IsConnected) return;

        if (!string.IsNullOrWhiteSpace(_connection.Username) && string.IsNullOrWhiteSpace(WatchedUser))
            WatchedUser = _connection.Username;

        RefreshCurrentJobsCommand.Execute(null);
    }

    public void Dispose()
    {
        if (_connection != null)
            _connection.PropertyChanged -= OnConnectionPropertyChanged;

        _pollTimer?.Stop();
        _pollTimer = null;

        _elapsedTimer.Tick -= OnElapsedTick;
        _elapsedTimer.Stop();
    }
}

/// <summary>Display row for a single Slurm job in the monitor tables.</summary>
public sealed class JobRow : ViewModelBase
{
    private string _state = string.Empty;
    private string _runTimeDisplay = string.Empty;
    private string _startTimeDisplay = string.Empty;
    private string _endTimeDisplay = string.Empty;

    public long JobId { get; set; }
    public string JobName { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public string State { get => _state; set => SetField(ref _state, value); }
    public string Partition { get; set; } = string.Empty;
    public string NodeList { get; set; } = string.Empty;

    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public TimeSpan? Elapsed { get; set; }
    public string? ExitCode { get; set; }
    public string? Reason { get; set; }
    public bool IsHistorical { get; set; }

    public string RunTimeDisplay
    {
        get => _runTimeDisplay;
        private set => SetField(ref _runTimeDisplay, value);
    }

    public string StartTimeDisplay
    {
        get => _startTimeDisplay;
        private set => SetField(ref _startTimeDisplay, value);
    }

    public string EndTimeDisplay
    {
        get => _endTimeDisplay;
        private set => SetField(ref _endTimeDisplay, value);
    }

    public void RefreshDisplays(DateTime now)
    {
        var elapsed = ResolveElapsed(now);
        RunTimeDisplay = elapsed.HasValue ? FormatElapsed(elapsed.Value) : string.Empty;
        StartTimeDisplay = FormatDateTime(StartTime);
        EndTimeDisplay = FormatDateTime(EndTime);
    }

    private TimeSpan? ResolveElapsed(DateTime now)
    {
        if (State is SlurmJobState.Running or SlurmJobState.Completing)
        {
            if (StartTime.HasValue)
            {
                var delta = now - StartTime.Value;
                return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
            }

            return Elapsed;
        }

        if (Elapsed.HasValue)
            return Elapsed;

        if (StartTime.HasValue && EndTime.HasValue)
        {
            var delta = EndTime.Value - StartTime.Value;
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        return null;
    }

    private static string FormatDateTime(DateTime? value)
        => value.HasValue ? value.Value.ToString("yyyy-MM-dd HH:mm:ss") : string.Empty;

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalDays >= 1)
            return $"{(int)elapsed.TotalDays}.{elapsed:hh\\:mm\\:ss}";
        return elapsed.ToString(@"hh\:mm\:ss");
    }
}
