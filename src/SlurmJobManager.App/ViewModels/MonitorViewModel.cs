using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Models;
using SlurmJobManager.Infrastructure.Resilience;

namespace SlurmJobManager.App.ViewModels;

/// <summary>
/// Polls squeue for a given user and exposes the job list with
/// status filtering, keyword search, and auto-poll toggle.
/// Supports connection state tracking and automatic reconnect.
/// </summary>
public sealed class MonitorViewModel : ViewModelBase, IDisposable
{
    private readonly ISlurmService      _slurm;
    private readonly AppSettings        _settings;
    private readonly IAppLogger?        _logger;
    private readonly ConnectionViewModel? _connection;

    // Polling infrastructure
    private DispatcherTimer? _timer;
    private int  _consecutiveFailures;
    private bool _isRefreshing;          // reentrancy guard

    // All jobs from the last refresh (unfiltered source of truth)
    private List<JobRow> _allJobs = new();

    private string _watchedUser = string.Empty;
    private int _pollIntervalSeconds = 3;
    private bool _isPolling;
    private bool _showAllUsers;
    private string _statusMessage = string.Empty;
    private string _statusStyleKey = "InfoTextStyle";
    private JobRow? _selectedJob;
    private string _allStatusFilter = string.Empty;
    private string _statusFilter = string.Empty;
    private string _searchText = string.Empty;

    public string WatchedUser      { get => _watchedUser;         set { if (SetField(ref _watchedUser, value)) OnPropertyChanged(nameof(IsEmptyState)); } }
    public int PollIntervalSeconds { get => _pollIntervalSeconds; set { SetField(ref _pollIntervalSeconds, value); UpdateTimerInterval(); } }
    public bool IsPolling          { get => _isPolling;           private set => SetField(ref _isPolling, value); }
    public string StatusMessage    { get => _statusMessage;       set => SetField(ref _statusMessage, value); }
    public string StatusStyleKey   { get => _statusStyleKey;      private set => SetField(ref _statusStyleKey, value); }
    public JobRow? SelectedJob     { get => _selectedJob;         set => SetField(ref _selectedJob, value); }

    /// <summary>When true, squeue is queried without a user filter (all users).</summary>
    public bool ShowAllUsers
    {
        get => _showAllUsers;
        set
        {
            if (SetField(ref _showAllUsers, value))
            {
                OnPropertyChanged(nameof(IsEmptyState));
                // Immediately refresh with the new scope
                RefreshCommand.Execute(null);
            }
        }
    }

    /// <summary>True when no watched user has been configured yet — used to drive the empty-state overlay.</summary>
    public bool IsEmptyState => !ShowAllUsers && string.IsNullOrWhiteSpace(WatchedUser);

    public string StatusFilter
    {
        get => _statusFilter;
        set { if (SetField(ref _statusFilter, value)) ApplyFilter(); }
    }

    public string SearchText
    {
        get => _searchText;
        set { if (SetField(ref _searchText, value)) ApplyFilter(); }
    }

    /// <summary>Available status filter options.</summary>
    public ObservableCollection<string> StatusFilterOptions { get; } = new();

    /// <summary>Filtered view displayed in the DataGrid.</summary>
    public ObservableCollection<JobRow> Jobs { get; } = new();

    public ICommand RefreshCommand      { get; }
    public ICommand StartPollingCommand { get; }
    public ICommand StopPollingCommand  { get; }
    public ICommand CancelJobCommand    { get; }

    public MonitorViewModel(
        ISlurmService slurm,
        AppSettings? settings = null,
        IAppLogger? logger = null,
        ConnectionViewModel? connection = null)
    {
        _slurm      = slurm      ?? throw new ArgumentNullException(nameof(slurm));
        _settings   = settings   ?? new AppSettings();
        _logger     = logger;
        _connection = connection;
        ResetFilterOptions();
        _statusFilter = _allStatusFilter;

        RefreshCommand      = new AsyncRelayCommand(RefreshAsync);
        StartPollingCommand = new RelayCommand(StartPolling, () => !IsPolling);
        StopPollingCommand  = new RelayCommand(StopPolling,  () => IsPolling);
        CancelJobCommand    = new AsyncRelayCommand(CancelSelectedJobAsync, () => SelectedJob != null);

        // Subscribe to connection changes so we can auto-fill the watched user and refresh
        if (_connection != null)
        {
            _connection.PropertyChanged += OnConnectionPropertyChanged;
            // Seed initial state if already connected at construction time
            if (_connection.IsConnected && !string.IsNullOrWhiteSpace(_connection.Username) && string.IsNullOrWhiteSpace(WatchedUser))
                WatchedUser = _connection.Username;
        }

        // Show a friendly empty-state hint until the user configures a watched user
        SetStatus("Monitor.EmptyState", "InfoTextStyle");
    }

    // ── Refresh ──────────────────────────────────────────────────────────────

    private async Task RefreshAsync(CancellationToken ct)
    {
        if (!ShowAllUsers && string.IsNullOrWhiteSpace(WatchedUser))
        {
            SetStatus("Monitor.EmptyState", "InfoTextStyle");
            return;
        }

        SetStatus("Monitor.Refreshing", "InfoTextStyle");
        try
        {
            IReadOnlyList<SlurmJobStatus> jobs;
            if (ShowAllUsers)
                jobs = await _slurm.GetAllJobsAsync(ct);
            else
                jobs = await _slurm.GetUserJobsAsync(WatchedUser, ct);

            Application.Current.Dispatcher.Invoke(() =>
            {
                _allJobs = jobs.Select(j => new JobRow
                {
                    JobId     = j.JobId,
                    JobName   = j.JobName,
                    User      = j.User,
                    State     = j.State,
                    Partition = j.Partition,
                    RunTime   = j.RunTime?.ToString(@"hh\:mm\:ss") ?? string.Empty,
                    NodeList  = j.NodeList ?? string.Empty,
                }).ToList();
                ApplyFilter();
            });
            _consecutiveFailures = 0;
            var scope = ShowAllUsers ? "all users" : $"'{WatchedUser}'";
            SetStatus(string.Format(L("Monitor.Updated"), DateTime.Now, jobs.Count), "SuccessTextStyle", localize: false);
            _logger?.Debug($"Monitor refreshed: {jobs.Count} job(s) for {scope}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            var msg = ConnectionViewModel.ClassifyError(ex);
            SetStatus(string.Format(L("Monitor.RefreshFailed"), msg), "ErrorTextStyle", localize: false);
            _logger?.Warning($"Monitor refresh failed ({_consecutiveFailures}× consecutive): {ex.Message}");
        }
    }

    // ── Polling (DispatcherTimer, reentrancy-safe) ───────────────────────────

    private void StartPolling()
    {
        if (IsPolling) return;
        _consecutiveFailures = 0;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_pollIntervalSeconds) };
        _timer.Tick += OnPollTick;
        _timer.Start();
        IsPolling = true;
        SetStatus(string.Format(L("Monitor.PollingEvery"), _pollIntervalSeconds), "InfoTextStyle", localize: false);
        _logger?.Info($"Monitor polling started for user '{WatchedUser}'");
    }

    private void StopPolling()
    {
        _timer?.Stop();
        _timer = null;
        IsPolling = false;
        SetStatus("Monitor.PollingStopped", "InfoTextStyle");
        _logger?.Info("Monitor polling stopped.");
    }

    private async void OnPollTick(object? sender, EventArgs e)
    {
        // Reentrancy guard — skip if a refresh is already in flight
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            // Check for too many failures → enter Error state and stop polling
            if (_consecutiveFailures >= _settings.MaxReconnectAttempts)
            {
                StopPolling();
                SetStatus(string.Format(L("Monitor.PollingHalted"), _consecutiveFailures), "ErrorTextStyle", localize: false);
                _logger?.Error($"Polling halted: {_consecutiveFailures} consecutive failures.");
                if (_connection is not null)
                    _connection.Status = ConnectionStatus.Error;
                return;
            }

            // If SSH is known to be disconnected, attempt a reconnect first
            if (_connection is not null && !_connection.IsConnected)
            {
                _connection.Status = ConnectionStatus.Reconnecting;
                _logger?.Info("SSH disconnected — attempting reconnect…");
                // Use a bounded timeout so reconnect doesn't block indefinitely
                using var reconnectCts = new CancellationTokenSource(_settings.ConnectionTimeout);
                bool reconnected = await _connection.TryReconnectAsync(reconnectCts.Token);
                if (!reconnected)
                {
                    _consecutiveFailures++;
                    SetStatus(string.Format(L("Monitor.ReconnectFailed"), _consecutiveFailures, _settings.MaxReconnectAttempts), "WarningTextStyle", localize: false);
                    _logger?.Warning($"Reconnect attempt failed ({_consecutiveFailures}).");
                    return;
                }
                _logger?.Info("Reconnected successfully — resuming polling.");
            }

            await RefreshAsync(CancellationToken.None);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void ApplyFilter()
    {
        var filtered = _allJobs.AsEnumerable();

        if (StatusFilter != _allStatusFilter)
            filtered = filtered.Where(j => j.State.Equals(StatusFilter, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = SearchText.Trim();
            // If the query parses as a number, compare against JobId directly; otherwise name-search only.
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

        Jobs.Clear();
        foreach (var row in filtered) Jobs.Add(row);
    }

    private async Task CancelSelectedJobAsync(CancellationToken ct)
    {
        if (SelectedJob == null)
        {
            SetStatus("Err.NoJobSelected", "WarningTextStyle");
            return;
        }

        var jobId = SelectedJob.JobId;
        var confirmTemplate = L("Monitor.CancelConfirm");
        var confirmTitle = L("Monitor.CancelConfirmTitle");
        var confirm = MessageBox.Show(
            string.Format(confirmTemplate, jobId),
            confirmTitle,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
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
            await RefreshAsync(ct);
        }
        catch (Exception ex)
        {
            SetStatus(string.Format(L("Monitor.CancelFailed"), jobId, ex.Message), "ErrorTextStyle", localize: false);
        }
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
    }

    private void SetStatus(string messageOrKey, string styleKey, bool localize = true)
    {
        StatusStyleKey = styleKey;
        StatusMessage = localize ? L(messageOrKey) : messageOrKey;
    }

    private void UpdateTimerInterval()
    {
        if (_timer != null)
            _timer.Interval = TimeSpan.FromSeconds(_pollIntervalSeconds);
    }

    private void OnConnectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(ConnectionViewModel.IsConnected)) return;
        if (_connection is null || !_connection.IsConnected) return;

        // Auto-fill watched user with the SSH login username on first connect
        if (!string.IsNullOrWhiteSpace(_connection.Username) && string.IsNullOrWhiteSpace(WatchedUser))
            WatchedUser = _connection.Username;

        // Trigger an initial refresh so data appears without manual interaction
        RefreshCommand.Execute(null);
    }

    public void Dispose()
    {
        if (_connection != null)
            _connection.PropertyChanged -= OnConnectionPropertyChanged;
        _timer?.Stop();
        _timer = null;
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
