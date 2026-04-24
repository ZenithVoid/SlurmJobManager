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
    private string _statusMessage = string.Empty;
    private JobRow? _selectedJob;
    private string _statusFilter = "All";
    private string _searchText = string.Empty;

    public string WatchedUser      { get => _watchedUser;         set => SetField(ref _watchedUser, value); }
    public int PollIntervalSeconds { get => _pollIntervalSeconds; set { SetField(ref _pollIntervalSeconds, value); UpdateTimerInterval(); } }
    public bool IsPolling          { get => _isPolling;           private set => SetField(ref _isPolling, value); }
    public string StatusMessage    { get => _statusMessage;       set => SetField(ref _statusMessage, value); }
    public JobRow? SelectedJob     { get => _selectedJob;         set => SetField(ref _selectedJob, value); }

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
    public IReadOnlyList<string> StatusFilterOptions { get; } =
        new[] { "All", SlurmJobState.Pending, SlurmJobState.Running, SlurmJobState.Completed,
                SlurmJobState.Failed, SlurmJobState.Cancelled };

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

        RefreshCommand      = new AsyncRelayCommand(RefreshAsync);
        StartPollingCommand = new RelayCommand(StartPolling, () => !IsPolling);
        StopPollingCommand  = new RelayCommand(StopPolling,  () => IsPolling);
        CancelJobCommand    = new AsyncRelayCommand(CancelSelectedJobAsync, () => SelectedJob != null);
    }

    // ── Refresh ──────────────────────────────────────────────────────────────

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
            StatusMessage = $"Updated: {DateTime.Now:HH:mm:ss}  ({jobs.Count} job(s))";
            _logger?.Debug($"Monitor refreshed: {jobs.Count} job(s) for '{WatchedUser}'");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            var msg = ConnectionViewModel.ClassifyError(ex);
            StatusMessage = $"Refresh failed: {msg}";
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
        StatusMessage = $"Polling every {_pollIntervalSeconds}s…";
        _logger?.Info($"Monitor polling started for user '{WatchedUser}'");
    }

    private void StopPolling()
    {
        _timer?.Stop();
        _timer = null;
        IsPolling = false;
        StatusMessage = "Polling stopped.";
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
                StatusMessage = $"Polling stopped after {_consecutiveFailures} consecutive failures. "
                              + "Check connection and restart polling manually.";
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
                bool reconnected = await _connection.TryReconnectAsync(CancellationToken.None);
                if (!reconnected)
                {
                    _consecutiveFailures++;
                    StatusMessage = $"Reconnect failed ({_consecutiveFailures}/{_settings.MaxReconnectAttempts})…";
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

        if (StatusFilter != "All")
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
        if (SelectedJob == null) return;
        StatusMessage = $"Cancelling job {SelectedJob.JobId}…";
        try
        {
            await _slurm.CancelJobAsync(SelectedJob.JobId, ct);
            StatusMessage = $"Job {SelectedJob.JobId} cancelled.";
            _logger?.Info($"Job {SelectedJob.JobId} cancelled by user.");
            await RefreshAsync(ct);
        }
        catch (Exception ex) { StatusMessage = $"Cancel failed: {ex.Message}"; }
    }

    private void UpdateTimerInterval()
    {
        if (_timer != null)
            _timer.Interval = TimeSpan.FromSeconds(_pollIntervalSeconds);
    }

    public void Dispose()
    {
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
