using System.Collections.ObjectModel;
using System.Windows.Input;

namespace SlurmJobManager.App.ViewModels;

/// <summary>
/// ViewModel for the chunked log viewer.
/// Exposes three paging commands: load older, load newer, jump to latest.
/// </summary>
public sealed class LogViewerViewModel : ViewModelBase
{
    private string _remoteFilePath = string.Empty;
    private int _chunkSize = 500;
    private bool _isBusy;
    private bool _isAtStart;
    private bool _isAtEnd;
    private long _startLine;
    private long _endLine;
    private long _totalLines;

    public string RemoteFilePath
    {
        get => _remoteFilePath;
        set => SetField(ref _remoteFilePath, value);
    }

    public int ChunkSize
    {
        get => _chunkSize;
        set => SetField(ref _chunkSize, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetField(ref _isBusy, value);
    }

    public bool IsAtStart
    {
        get => _isAtStart;
        private set => SetField(ref _isAtStart, value);
    }

    public bool IsAtEnd
    {
        get => _isAtEnd;
        private set => SetField(ref _isAtEnd, value);
    }

    public long StartLine
    {
        get => _startLine;
        private set => SetField(ref _startLine, value);
    }

    public long EndLine
    {
        get => _endLine;
        private set => SetField(ref _endLine, value);
    }

    public long TotalLines
    {
        get => _totalLines;
        private set => SetField(ref _totalLines, value);
    }

    /// <summary>Current page of log lines displayed in the view.</summary>
    public ObservableCollection<string> Lines { get; } = new();

    public ICommand LoadLatestCommand { get; }
    public ICommand LoadOlderCommand { get; }
    public ICommand LoadNewerCommand { get; }
    public ICommand OpenFileCommand { get; }

    public LogViewerViewModel()
    {
        LoadLatestCommand = new RelayCommand(LoadLatest, () => !IsBusy);
        LoadOlderCommand = new RelayCommand(LoadOlder, () => !IsBusy && !IsAtStart);
        LoadNewerCommand = new RelayCommand(LoadNewer, () => !IsBusy && !IsAtEnd);
        OpenFileCommand = new RelayCommand(OpenFile);
    }

    private void LoadLatest()
    {
        // TODO: call ILogChunkService.GetLatestChunkAsync and populate Lines
    }

    private void LoadOlder()
    {
        // TODO: call ILogChunkService.GetOlderChunkAsync with AnchorLine = StartLine
    }

    private void LoadNewer()
    {
        // TODO: call ILogChunkService.GetNewerChunkAsync with AnchorLine = EndLine
    }

    private void OpenFile()
    {
        // TODO: open OpenFileDialog (remote path input) or browse SFTP tree
    }

    private void ApplyChunk(Core.Models.LogChunkResult result)
    {
        Lines.Clear();
        foreach (var line in result.Lines)
            Lines.Add(line);

        StartLine = result.StartLine;
        EndLine = result.EndLine;
        TotalLines = result.TotalLines;
        IsAtStart = result.IsAtStart;
        IsAtEnd = result.IsAtEnd;
    }
}
