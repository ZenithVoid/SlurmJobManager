using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Models;

namespace SlurmJobManager.App.ViewModels;

/// <summary>
/// Chunked log viewer with:
/// - Bounded in-memory chunk cache (max <see cref="MaxCachedChunks"/> chunks).
/// - Follow mode: polls for new lines every <see cref="FollowIntervalSeconds"/> seconds.
/// - In-buffer search across currently loaded lines.
/// - Range display showing current window and approximate total.
/// - Cancel support for ongoing load operations.
/// - Error preservation: UI content is NOT cleared when a fetch fails.
/// </summary>
public sealed class LogViewerViewModel : ViewModelBase, IDisposable
{
    private readonly ILogChunkService _logChunks;
    private readonly IAppLogger?      _logger;
    private readonly object _loadLock = new();
    private bool _loadInProgress;
    private CancellationTokenSource? _loadCts;

    // Bounded cache: each entry is one loaded chunk.  List is oldest→newest.
    private const int MaxCachedChunks = 20;
    private readonly List<LogChunkResult> _chunkCache = new();

    private DispatcherTimer? _followTimer;

    private string _remoteFilePath = string.Empty;
    private int _chunkSize = 200;
    private bool _isBusy;
    private bool _isAtStart;
    private bool _isAtEnd;
    private long _startLine;
    private long _endLine;
    private long _totalLines;
    private string _statusMessage = string.Empty;
    private bool _followMode;
    private int _followIntervalSeconds = 3;
    private string _searchText = string.Empty;

    public string RemoteFilePath { get => _remoteFilePath; set => SetField(ref _remoteFilePath, value); }
    public int ChunkSize         { get => _chunkSize;       set => SetField(ref _chunkSize, value); }
    public bool IsBusy           { get => _isBusy;          private set => SetField(ref _isBusy, value); }
    public bool IsAtStart        { get => _isAtStart;       private set => SetField(ref _isAtStart, value); }
    public bool IsAtEnd          { get => _isAtEnd;         private set => SetField(ref _isAtEnd, value); }
    public long StartLine        { get => _startLine;       private set => SetField(ref _startLine, value); }
    public long EndLine          { get => _endLine;         private set => SetField(ref _endLine, value); }
    public long TotalLines       { get => _totalLines;      private set => SetField(ref _totalLines, value); }
    public string StatusMessage  { get => _statusMessage;   set => SetField(ref _statusMessage, value); }
    public int FollowIntervalSeconds { get => _followIntervalSeconds; set { SetField(ref _followIntervalSeconds, value); UpdateFollowTimerInterval(); } }

    public bool FollowMode
    {
        get => _followMode;
        set
        {
            if (!SetField(ref _followMode, value)) return;
            if (value) StartFollowTimer();
            else StopFollowTimer();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set { if (SetField(ref _searchText, value)) ApplySearch(); }
    }

    public string RangeText =>
        _totalLines > 0
            ? $"Showing {_startLine}–{_endLine} / ~{_totalLines:N0} lines"
            : "No data loaded";

    public string CacheText =>
        _chunkCache.Count > 0
            ? $"Cache: {_chunkCache.Count}/{MaxCachedChunks} chunk(s)"
            : string.Empty;

    /// <summary>All lines currently rendered (after search filter).</summary>
    public ObservableCollection<string> Lines { get; } = new();

    public ICommand LoadLatestCommand  { get; }
    public ICommand LoadOlderCommand   { get; }
    public ICommand LoadNewerCommand   { get; }
    public ICommand ClearCacheCommand  { get; }
    public ICommand CancelLoadCommand  { get; }

    public LogViewerViewModel(ILogChunkService logChunks, IAppLogger? logger = null)
    {
        _logChunks = logChunks ?? throw new ArgumentNullException(nameof(logChunks));
        _logger    = logger;

        LoadLatestCommand = new AsyncRelayCommand(LoadLatestAsync, () => !IsBusy);
        LoadOlderCommand  = new AsyncRelayCommand(LoadOlderAsync,  () => !IsBusy && !IsAtStart);
        LoadNewerCommand  = new AsyncRelayCommand(LoadNewerAsync,  () => !IsBusy && !IsAtEnd);
        ClearCacheCommand = new RelayCommand(ClearCache);
        CancelLoadCommand = new RelayCommand(CancelLoad, () => IsBusy);
    }

    // ── Load commands ────────────────────────────────────────────────────────

    private async Task LoadLatestAsync(CancellationToken ct)
    {
        if (!AcquireLoad(ct)) return;
        try
        {
            var req    = new LogChunkRequest { RemoteFilePath = RemoteFilePath, ChunkSize = ChunkSize };
            var result = await _logChunks.GetLatestChunkAsync(req, _loadCts!.Token);
            AddChunkToCache(result);
            RenderFromCache();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Load cancelled.";
        }
        catch (Exception ex)
        {
            // Preserve existing UI content — only update status message
            _logger?.Warning($"Log fetch failed: {ex.Message}");
            StatusMessage = ClassifyLogError(ex);
        }
        finally { ReleaseLoad(); }
    }

    private async Task LoadOlderAsync(CancellationToken ct)
    {
        if (!AcquireLoad(ct)) return;
        try
        {
            var req    = new LogChunkRequest { RemoteFilePath = RemoteFilePath, ChunkSize = ChunkSize, AnchorLine = StartLine };
            var result = await _logChunks.GetOlderChunkAsync(req, _loadCts!.Token);
            AddChunkToCache(result, prepend: true);
            RenderFromCache();
        }
        catch (OperationCanceledException) { StatusMessage = "Load cancelled."; }
        catch (Exception ex)
        {
            _logger?.Warning($"Log fetch (older) failed: {ex.Message}");
            StatusMessage = ClassifyLogError(ex);
        }
        finally { ReleaseLoad(); }
    }

    private async Task LoadNewerAsync(CancellationToken ct)
    {
        if (!AcquireLoad(ct)) return;
        try
        {
            var req    = new LogChunkRequest { RemoteFilePath = RemoteFilePath, ChunkSize = ChunkSize, AnchorLine = EndLine };
            var result = await _logChunks.GetNewerChunkAsync(req, _loadCts!.Token);
            AddChunkToCache(result);
            RenderFromCache();
        }
        catch (OperationCanceledException) { StatusMessage = "Load cancelled."; }
        catch (Exception ex)
        {
            _logger?.Warning($"Log fetch (newer) failed: {ex.Message}");
            StatusMessage = ClassifyLogError(ex);
        }
        finally { ReleaseLoad(); }
    }

    // ── Follow mode ──────────────────────────────────────────────────────────

    private void StartFollowTimer()
    {
        _followTimer?.Stop();
        _followTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_followIntervalSeconds) };
        _followTimer.Tick += async (_, _) => await FollowTickAsync();
        _followTimer.Start();
    }

    private void StopFollowTimer()
    {
        _followTimer?.Stop();
        _followTimer = null;
    }

    private void UpdateFollowTimerInterval()
    {
        if (_followTimer != null)
            _followTimer.Interval = TimeSpan.FromSeconds(_followIntervalSeconds);
    }

    private async Task FollowTickAsync()
    {
        if (!AcquireLoad(CancellationToken.None)) return;
        try
        {
            var req    = new LogChunkRequest { RemoteFilePath = RemoteFilePath, ChunkSize = ChunkSize, AnchorLine = EndLine };
            var result = await _logChunks.GetNewerChunkAsync(req, _loadCts!.Token);
            if (result.Lines.Count > 0)
            {
                AddChunkToCache(result);
                RenderFromCache(scrollToEnd: true);
            }
            else
            {
                StatusMessage = $"Follow: up-to-date at {DateTime.Now:HH:mm:ss}";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.Warning($"Follow tick error: {ex.Message}");
            // Preserve existing content; just update the status
            StatusMessage = $"Follow error (content preserved): {ClassifyLogError(ex)}";
        }
        finally { ReleaseLoad(); }
    }

    // ── Cancel ───────────────────────────────────────────────────────────────

    private void CancelLoad()
    {
        _loadCts?.Cancel();
        StatusMessage = "Cancelling…";
    }

    // ── Chunk cache management ───────────────────────────────────────────────

    private void AddChunkToCache(LogChunkResult chunk, bool prepend = false)
    {
        if (prepend)
            _chunkCache.Insert(0, chunk);
        else
            _chunkCache.Add(chunk);

        // Evict oldest chunks when limit exceeded
        while (_chunkCache.Count > MaxCachedChunks)
        {
            if (prepend)
                _chunkCache.RemoveAt(_chunkCache.Count - 1);
            else
                _chunkCache.RemoveAt(0);
        }
    }

    private void ClearCache()
    {
        _chunkCache.Clear();
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            Lines.Clear();
            StartLine  = 0;
            EndLine    = 0;
            TotalLines = 0;
            IsAtStart  = false;
            IsAtEnd    = false;
            StatusMessage = "Cache cleared.";
            OnPropertyChanged(nameof(RangeText));
            OnPropertyChanged(nameof(CacheText));
        });
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    private void RenderFromCache(bool scrollToEnd = false)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var allLines = _chunkCache.SelectMany(c => c.Lines).ToList();

            // Apply search filter
            var filtered = string.IsNullOrWhiteSpace(_searchText)
                ? allLines
                : allLines.Where(l => l.Contains(_searchText, StringComparison.OrdinalIgnoreCase)).ToList();

            Lines.Clear();
            foreach (var line in filtered) Lines.Add(line);

            if (_chunkCache.Count > 0)
            {
                StartLine  = _chunkCache[0].StartLine;
                EndLine    = _chunkCache[^1].EndLine;
                TotalLines = _chunkCache[^1].TotalLines;
                IsAtStart  = _chunkCache[0].IsAtStart;
                IsAtEnd    = _chunkCache[^1].IsAtEnd;
            }

            StatusMessage = string.IsNullOrWhiteSpace(_searchText)
                ? string.Empty
                : $"Search: {filtered.Count}/{allLines.Count} match(es)";

            OnPropertyChanged(nameof(RangeText));
            OnPropertyChanged(nameof(CacheText));
        });
    }

    private void ApplySearch() => RenderFromCache();

    // ── Locking helpers ──────────────────────────────────────────────────────

    private bool AcquireLoad(CancellationToken externalCt)
    {
        lock (_loadLock)
        {
            if (_loadInProgress) return false;
            _loadInProgress = true;
        }
        // Defensively dispose any stale CTS before creating a new one
        _loadCts?.Dispose();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        IsBusy = true;
        StatusMessage = "Loading…";
        return true;
    }

    private void ReleaseLoad()
    {
        _loadCts?.Dispose();
        _loadCts = null;
        lock (_loadLock) { _loadInProgress = false; }
        IsBusy = false;
    }

    // ── Error classification ─────────────────────────────────────────────────

    private static string ClassifyLogError(Exception ex)
    {
        if (ex.Message.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("not found",    StringComparison.OrdinalIgnoreCase))
            return "Remote file not found — check the file path.";
        if (ex is TimeoutException)
            return "Log fetch timed out.";
        if (ex is InvalidOperationException && ex.Message.Contains("not connected"))
            return "SSH not connected — reconnect and retry.";
        return $"Fetch error: {ex.Message}";
    }

    public void Dispose()
    {
        StopFollowTimer();
        _loadCts?.Cancel();
        _loadCts?.Dispose();
    }
}
