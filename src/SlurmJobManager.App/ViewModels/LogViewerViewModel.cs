using System.Collections.ObjectModel;
using System.Windows.Input;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Models;

namespace SlurmJobManager.App.ViewModels;

/// <summary>
/// Chunked log viewer: loads at most <see cref="ChunkSize"/> lines at a time
/// and prevents concurrent requests with a simple lock.
/// </summary>
public sealed class LogViewerViewModel : ViewModelBase
{
    private readonly ILogChunkService _logChunks;
    private readonly object _loadLock = new();
    private bool _loadInProgress;

    private string _remoteFilePath = string.Empty;
    private int _chunkSize = 200;
    private bool _isBusy;
    private bool _isAtStart;
    private bool _isAtEnd;
    private long _startLine;
    private long _endLine;
    private long _totalLines;
    private string _statusMessage = string.Empty;

    public string RemoteFilePath { get => _remoteFilePath; set => SetField(ref _remoteFilePath, value); }
    public int ChunkSize         { get => _chunkSize;       set => SetField(ref _chunkSize, value); }
    public bool IsBusy           { get => _isBusy;          private set => SetField(ref _isBusy, value); }
    public bool IsAtStart        { get => _isAtStart;       private set => SetField(ref _isAtStart, value); }
    public bool IsAtEnd          { get => _isAtEnd;         private set => SetField(ref _isAtEnd, value); }
    public long StartLine        { get => _startLine;       private set => SetField(ref _startLine, value); }
    public long EndLine          { get => _endLine;         private set => SetField(ref _endLine, value); }
    public long TotalLines       { get => _totalLines;      private set => SetField(ref _totalLines, value); }
    public string StatusMessage  { get => _statusMessage;   set => SetField(ref _statusMessage, value); }

    public string RangeText =>
        _totalLines > 0 ? $"Showing lines {_startLine}–{_endLine} / {_totalLines}" : "No data";

    public ObservableCollection<string> Lines { get; } = new();

    public ICommand LoadLatestCommand { get; }
    public ICommand LoadOlderCommand  { get; }
    public ICommand LoadNewerCommand  { get; }

    public LogViewerViewModel(ILogChunkService logChunks)
    {
        _logChunks = logChunks ?? throw new ArgumentNullException(nameof(logChunks));

        LoadLatestCommand = new AsyncRelayCommand(LoadLatestAsync, () => !IsBusy);
        LoadOlderCommand  = new AsyncRelayCommand(LoadOlderAsync,  () => !IsBusy && !IsAtStart);
        LoadNewerCommand  = new AsyncRelayCommand(LoadNewerAsync,  () => !IsBusy && !IsAtEnd);
    }

    private async Task LoadLatestAsync(CancellationToken ct)
    {
        if (!AcquireLoad()) return;
        try
        {
            var req    = new LogChunkRequest { RemoteFilePath = RemoteFilePath, ChunkSize = ChunkSize };
            var result = await _logChunks.GetLatestChunkAsync(req, ct);
            ApplyChunk(result);
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { ReleaseLoad(); }
    }

    private async Task LoadOlderAsync(CancellationToken ct)
    {
        if (!AcquireLoad()) return;
        try
        {
            var req    = new LogChunkRequest { RemoteFilePath = RemoteFilePath, ChunkSize = ChunkSize, AnchorLine = StartLine };
            var result = await _logChunks.GetOlderChunkAsync(req, ct);
            ApplyChunk(result);
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { ReleaseLoad(); }
    }

    private async Task LoadNewerAsync(CancellationToken ct)
    {
        if (!AcquireLoad()) return;
        try
        {
            var req    = new LogChunkRequest { RemoteFilePath = RemoteFilePath, ChunkSize = ChunkSize, AnchorLine = EndLine };
            var result = await _logChunks.GetNewerChunkAsync(req, ct);
            ApplyChunk(result);
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { ReleaseLoad(); }
    }

    private bool AcquireLoad()
    {
        lock (_loadLock)
        {
            if (_loadInProgress) return false;
            _loadInProgress = true;
        }
        IsBusy = true;
        StatusMessage = "Loading…";
        return true;
    }

    private void ReleaseLoad()
    {
        lock (_loadLock) { _loadInProgress = false; }
        IsBusy = false;
    }

    private void ApplyChunk(LogChunkResult result)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            Lines.Clear();
            foreach (var line in result.Lines) Lines.Add(line);
            StartLine  = result.StartLine;
            EndLine    = result.EndLine;
            TotalLines = result.TotalLines;
            IsAtStart  = result.IsAtStart;
            IsAtEnd    = result.IsAtEnd;
            StatusMessage = string.Empty;
            OnPropertyChanged(nameof(RangeText));
        });
    }
}
