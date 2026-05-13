using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SlurmJobManager.App.Services;
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
    private readonly INotificationService? _notificationService;
    private Func<JobRow?, string?>? _resolveHistoryWorkDir;
    private Func<string, CancellationToken, Task<bool>>? _openRemoteFileAsync;
    private Func<string, CancellationToken, Task<bool>>? _openInConsoleAsync;

    // Polling infrastructure
    private DispatcherTimer? _pollTimer;
    private readonly DispatcherTimer _elapsedTimer;
    private int _consecutiveFailures;
    private bool _isRefreshing;

    // Source-of-truth caches
    private List<JobRow> _allCurrentJobs = new();
    private Dictionary<long, JobRow> _lastCurrentSnapshot = new();
    private readonly HashSet<long> _notifiedCompletedJobIds = new();
    private readonly object _completionNotificationGate = new();

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
                OnPropertyChanged(nameof(MonitorContextSummary));
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
            {
                UpdatePollTimerInterval();
                OnPropertyChanged(nameof(MonitorContextSummary));
            }
        }
    }

    public bool IsPolling
    {
        get => _isPolling;
        private set
        {
            if (SetField(ref _isPolling, value))
            {
                OnPropertyChanged(nameof(MonitorContextSummary));
                OnPropertyChanged(nameof(EffectiveStatusStyleKey));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetField(ref _statusMessage, value))
                OnPropertyChanged(nameof(EffectiveStatusMessage));
        }
    }

    public string StatusStyleKey
    {
        get => _statusStyleKey;
        private set
        {
            if (SetField(ref _statusStyleKey, value))
                OnPropertyChanged(nameof(EffectiveStatusStyleKey));
        }
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
                OnPropertyChanged(nameof(MonitorContextSummary));
                CommandManager.InvalidateRequerySuggested();
                RefreshCommand.Execute(null);
            }
        }
    }

    /// <summary>True when no watched user has been configured yet.</summary>
    public bool IsEmptyState => !ShowAllUsers && string.IsNullOrWhiteSpace(WatchedUser);

    /// <summary>True when a concrete username is available for history lookup.</summary>
    public bool CanQueryHistory => !string.IsNullOrWhiteSpace(GetHistoryQueryUser());
    public string MonitorContextSummary
    {
        get
        {
            var scope = ShowAllUsers ? L("Monitor.ScopeAllUsers") : string.Format(L("Monitor.ScopeSingleUser"), WatchedUser.Trim());
            if (!ShowAllUsers && string.IsNullOrWhiteSpace(WatchedUser))
                scope = L("Monitor.ScopeUnset");
            var historyUser = GetHistoryQueryUser();
            var query = string.IsNullOrWhiteSpace(historyUser)
                ? L("Monitor.HistoryQueryUnset")
                : string.Format(L("Monitor.HistoryQueryUser"), historyUser);
            var refresh = IsPolling
                ? string.Format(L("Monitor.RefreshPolling"), PollIntervalSeconds)
                : L("Monitor.RefreshManual");
            return string.Format(L("Monitor.ContextSummary"), scope, query, refresh);
        }
    }
    public string EffectiveStatusStyleKey
        => (_isRefreshing || IsPolling) && StatusStyleKey == "InfoTextStyle"
            ? "BusyTextStyle"
            : StatusStyleKey;
    public string EffectiveStatusMessage
        => !string.IsNullOrWhiteSpace(StatusMessage)
            ? StatusMessage
            : (_isRefreshing ? L("Status.Loading") : string.Empty);

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
    public ICommand OpenHistoryStdoutCommand { get; }
    public ICommand OpenHistoryStderrCommand { get; }
    public ICommand OpenHistoryWorkDirCommand { get; }

    public MonitorViewModel(
        ISlurmService slurm,
        AppSettings? settings = null,
        IAppLogger? logger = null,
        ConnectionViewModel? connection = null,
        INotificationService? notificationService = null)
    {
        _slurm = slurm ?? throw new ArgumentNullException(nameof(slurm));
        _settings = settings ?? new AppSettings();
        _logger = logger;
        _connection = connection;
        _notificationService = notificationService;

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
        OpenHistoryStdoutCommand = new AsyncRelayCommand<JobRow>((row, ct) => OpenHistoryStdoutAsync(row, ct), row => row is not null);
        OpenHistoryStderrCommand = new AsyncRelayCommand<JobRow>((row, ct) => OpenHistoryStderrAsync(row, ct), row => row is not null);
        OpenHistoryWorkDirCommand = new AsyncRelayCommand<JobRow>((row, ct) => OpenHistoryWorkDirAsync(row, ct), row => row is not null);

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

            var mappedJobs = jobs
                .Select(MapCurrentJob)
                .OrderByDescending(j => j.JobId)
                .ToList();
            var completionNotifications = await DetectCompletionNotificationsAsync(mappedJobs, ct);

            Application.Current.Dispatcher.Invoke(() =>
            {
                RefreshCurrentJobsSnapshot(mappedJobs);
                ApplyCurrentFilter();
                UpdateCurrentElapsedDisplays();
            });
            NotifyCompletedJobs(completionNotifications);

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
        OnPropertyChanged(nameof(EffectiveStatusStyleKey));
        OnPropertyChanged(nameof(EffectiveStatusMessage));

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
            OnPropertyChanged(nameof(EffectiveStatusStyleKey));
            OnPropertyChanged(nameof(EffectiveStatusMessage));
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
        IEnumerable<JobRow> filtered = _allCurrentJobs;

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

        var filteredRows = filtered.ToList();
        SyncVisibleCollection(CurrentJobs, filteredRows);

        if (SelectedCurrentJob is not null && !filteredRows.Contains(SelectedCurrentJob))
            SelectedCurrentJob = null;
    }

    private void RefreshCurrentJobsSnapshot(IReadOnlyList<JobRow> latestJobs)
    {
        var existingById = _allCurrentJobs.ToDictionary(row => row.JobId);
        var merged = new List<JobRow>(latestJobs.Count);
        var now = DateTime.Now;

        foreach (var latest in latestJobs)
        {
            if (existingById.TryGetValue(latest.JobId, out var existing))
            {
                existing.UpdateFrom(latest, now);
                merged.Add(existing);
                existingById.Remove(latest.JobId);
                continue;
            }

            latest.RefreshDisplays(now);
            merged.Add(latest);
        }

        _allCurrentJobs = merged;
    }

    private static void SyncVisibleCollection<T>(ObservableCollection<T> target, IReadOnlyList<T> desired)
        where T : class
    {
        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (!desired.Contains(target[index]))
                target.RemoveAt(index);
        }

        for (var index = 0; index < desired.Count; index++)
        {
            var item = desired[index];
            if (index < target.Count && ReferenceEquals(target[index], item))
                continue;

            var existingIndex = target.IndexOf(item);
            if (existingIndex >= 0)
                target.Move(existingIndex, index);
            else
                target.Insert(index, item);
        }
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

        var detailLines = new List<string>
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
        };

        if (TryBuildHistoryDiagnosticPaths(row, out var workDir, out var stdoutPath, out var stderrPath))
        {
            detailLines.Add($"{L("Monitor.DetailTaskWorkDir")}: {workDir}");
            detailLines.Add($"{L("Monitor.DetailStdoutPath")}: {stdoutPath}");
            detailLines.Add($"{L("Monitor.DetailStderrPath")}: {stderrPath}");
        }
        else
        {
            detailLines.Add(L("Monitor.HistoryLogsHint"));
        }

        var details = string.Join(Environment.NewLine, detailLines);

        MessageBox.Show(details, L("Monitor.HistoryDetailTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void SetDiagnosticNavigationHandlers(
        Func<JobRow?, string?>? resolveHistoryWorkDir,
        Func<string, CancellationToken, Task<bool>>? openRemoteFileAsync,
        Func<string, CancellationToken, Task<bool>>? openInConsoleAsync)
    {
        _resolveHistoryWorkDir = resolveHistoryWorkDir;
        _openRemoteFileAsync = openRemoteFileAsync;
        _openInConsoleAsync = openInConsoleAsync;
    }

    private async Task OpenHistoryStdoutAsync(JobRow? row, CancellationToken ct)
    {
        if (row is null) return;
        if (_openRemoteFileAsync == null)
        {
            SetStatus("Monitor.HistoryOpenUnavailable", "WarningTextStyle");
            return;
        }

        if (!TryBuildHistoryDiagnosticPaths(row, out _, out var stdoutPath, out _))
        {
            SetStatus("Monitor.HistoryNoTaskContext", "WarningTextStyle");
            return;
        }

        var opened = await _openRemoteFileAsync(stdoutPath, ct);
        SetStatus(
            opened
                ? string.Format(L("Monitor.HistoryOpenedPath"), stdoutPath)
                : string.Format(L("Monitor.HistoryOpenPathFailed"), stdoutPath),
            opened ? "SuccessTextStyle" : "WarningTextStyle",
            localize: false);
    }

    private async Task OpenHistoryStderrAsync(JobRow? row, CancellationToken ct)
    {
        if (row is null) return;
        if (_openRemoteFileAsync == null)
        {
            SetStatus("Monitor.HistoryOpenUnavailable", "WarningTextStyle");
            return;
        }

        if (!TryBuildHistoryDiagnosticPaths(row, out _, out _, out var stderrPath))
        {
            SetStatus("Monitor.HistoryNoTaskContext", "WarningTextStyle");
            return;
        }

        var opened = await _openRemoteFileAsync(stderrPath, ct);
        SetStatus(
            opened
                ? string.Format(L("Monitor.HistoryOpenedPath"), stderrPath)
                : string.Format(L("Monitor.HistoryOpenPathFailed"), stderrPath),
            opened ? "SuccessTextStyle" : "WarningTextStyle",
            localize: false);
    }

    private async Task OpenHistoryWorkDirAsync(JobRow? row, CancellationToken ct)
    {
        if (row is null) return;
        if (_openInConsoleAsync == null)
        {
            SetStatus("Monitor.HistoryOpenUnavailable", "WarningTextStyle");
            return;
        }

        if (!TryBuildHistoryDiagnosticPaths(row, out var workDir, out _, out _))
        {
            SetStatus("Monitor.HistoryNoTaskContext", "WarningTextStyle");
            return;
        }

        var opened = await _openInConsoleAsync(workDir, ct);
        SetStatus(
            opened
                ? string.Format(L("Monitor.HistoryOpenedPath"), workDir)
                : string.Format(L("Monitor.HistoryOpenPathFailed"), workDir),
            opened ? "SuccessTextStyle" : "WarningTextStyle",
            localize: false);
    }

    private bool TryBuildHistoryDiagnosticPaths(JobRow? row, out string workDir, out string stdoutPath, out string stderrPath)
    {
        workDir = string.Empty;
        stdoutPath = string.Empty;
        stderrPath = string.Empty;
        if (row is null || _resolveHistoryWorkDir == null)
            return false;

        var resolved = _resolveHistoryWorkDir(row)?.Trim();
        if (string.IsNullOrWhiteSpace(resolved))
            return false;

        workDir = resolved.TrimEnd('/');
        stdoutPath = $"{workDir}/logs/job.out";
        stderrPath = $"{workDir}/logs/job.err";
        return true;
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

    private async Task<IReadOnlyList<JobRow>> DetectCompletionNotificationsAsync(
        IReadOnlyList<JobRow> currentSnapshot,
        CancellationToken ct)
    {
        Dictionary<long, JobRow> previousSnapshot;
        HashSet<long> notifiedSnapshot;
        lock (_completionNotificationGate)
        {
            previousSnapshot = new Dictionary<long, JobRow>(_lastCurrentSnapshot);
            notifiedSnapshot = new HashSet<long>(_notifiedCompletedJobIds);
        }

        var currentById = currentSnapshot
            .GroupBy(row => row.JobId)
            .ToDictionary(group => group.Key, group => group.First());

        if (previousSnapshot.Count == 0)
        {
            lock (_completionNotificationGate)
                _lastCurrentSnapshot = currentById;
            return Array.Empty<JobRow>();
        }

        var readyToNotify = new Dictionary<long, JobRow>();

        foreach (var row in currentById.Values)
        {
            if (notifiedSnapshot.Contains(row.JobId))
                continue;

            var hasPrevious = previousSnapshot.TryGetValue(row.JobId, out var previous);
            if (IsTerminalState(row.State) && (!hasPrevious || !IsTerminalState(previous!.State)))
                readyToNotify[row.JobId] = row;
        }

        var disappeared = previousSnapshot.Values
            .Where(previous => !currentById.ContainsKey(previous.JobId))
            .ToList();

        if (disappeared.Count > 0)
        {
            var resolved = await ResolveTerminalRowsFromHistoryAsync(disappeared, ct);
            foreach (var row in resolved)
            {
                if (!notifiedSnapshot.Contains(row.JobId))
                    readyToNotify[row.JobId] = row;
            }
        }

        lock (_completionNotificationGate)
            _lastCurrentSnapshot = currentById;
        return readyToNotify.Values.ToList();
    }

    private async Task<IReadOnlyList<JobRow>> ResolveTerminalRowsFromHistoryAsync(
        IReadOnlyList<JobRow> disappearedJobs,
        CancellationToken ct)
    {
        var disappearedById = disappearedJobs
            .Where(row => row.JobId > 0)
            .ToDictionary(row => row.JobId, row => row);
        if (disappearedById.Count == 0)
            return Array.Empty<JobRow>();

        var terminalById = new Dictionary<long, JobRow>();
        var users = disappearedJobs
            .Select(row => row.User?.Trim())
            .Where(user => !string.IsNullOrWhiteSpace(user))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var user in users)
        {
            try
            {
                var history = await _slurm.GetUserJobHistoryAsync(user!, HistoryFetchLimit, ct);
                foreach (var historyStatus in history)
                {
                    if (!disappearedById.ContainsKey(historyStatus.JobId) || !IsTerminalState(historyStatus.State))
                        continue;
                    if (!terminalById.ContainsKey(historyStatus.JobId))
                        terminalById[historyStatus.JobId] = MapHistoricalJob(historyStatus);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warning($"Monitor completion history lookup failed for user '{user}': {ex.Message}");
            }
        }

        return terminalById.Values.ToList();
    }

    private void NotifyCompletedJobs(IReadOnlyList<JobRow> completedJobs)
    {
        if (completedJobs.Count == 0)
            return;

        foreach (var row in completedJobs.OrderBy(r => r.JobId))
        {
            lock (_completionNotificationGate)
            {
                if (_notifiedCompletedJobIds.Contains(row.JobId))
                    continue;
                _notifiedCompletedJobIds.Add(row.JobId);
            }

            try
            {
                _notificationService?.Show(
                    L("Monitor.JobCompletionNotificationTitle"),
                    string.Format(
                        L("Monitor.JobCompletionNotificationBody"),
                        row.JobId,
                        ValueOrDash(row.JobName),
                        ValueOrDash(row.State)));
            }
            catch (Exception ex)
            {
                _logger?.Warning($"Monitor completion notification failed for job {row.JobId}: {ex.Message}");
            }
        }
    }

    private static bool IsTerminalState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
            return false;

        var value = state.Trim();
        return value.Equals(SlurmJobState.Completed, StringComparison.OrdinalIgnoreCase)
            || value.Equals(SlurmJobState.Failed, StringComparison.OrdinalIgnoreCase)
            || value.Equals(SlurmJobState.Cancelled, StringComparison.OrdinalIgnoreCase)
            || value.Equals(SlurmJobState.Timeout, StringComparison.OrdinalIgnoreCase)
            || value.Equals(SlurmJobState.NodeFail, StringComparison.OrdinalIgnoreCase);
    }

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
        OnPropertyChanged(nameof(MonitorContextSummary));
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
    private string _jobName = string.Empty;
    private string _user = string.Empty;
    private string _state = string.Empty;
    private string _partition = string.Empty;
    private string _nodeList = string.Empty;
    private DateTime? _startTime;
    private DateTime? _endTime;
    private TimeSpan? _elapsed;
    private string? _exitCode;
    private string? _reason;
    private bool _isHistorical;
    private string _runTimeDisplay = string.Empty;
    private string _startTimeDisplay = string.Empty;
    private string _endTimeDisplay = string.Empty;

    public long JobId { get; set; }
    public string JobName { get => _jobName; set => SetField(ref _jobName, value); }
    public string User { get => _user; set => SetField(ref _user, value); }
    public string State { get => _state; set => SetField(ref _state, value); }
    public string Partition { get => _partition; set => SetField(ref _partition, value); }
    public string NodeList { get => _nodeList; set => SetField(ref _nodeList, value); }

    public DateTime? StartTime { get => _startTime; set => SetField(ref _startTime, value); }
    public DateTime? EndTime { get => _endTime; set => SetField(ref _endTime, value); }
    public TimeSpan? Elapsed { get => _elapsed; set => SetField(ref _elapsed, value); }
    public string? ExitCode { get => _exitCode; set => SetField(ref _exitCode, value); }
    public string? Reason { get => _reason; set => SetField(ref _reason, value); }
    public bool IsHistorical { get => _isHistorical; set => SetField(ref _isHistorical, value); }

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

    public void UpdateFrom(JobRow latest, DateTime now)
    {
        JobName = latest.JobName;
        User = latest.User;
        State = latest.State;
        Partition = latest.Partition;
        NodeList = latest.NodeList;
        StartTime = latest.StartTime;
        EndTime = latest.EndTime;
        Elapsed = latest.Elapsed;
        ExitCode = latest.ExitCode;
        Reason = latest.Reason;
        IsHistorical = latest.IsHistorical;
        RefreshDisplays(now);
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
