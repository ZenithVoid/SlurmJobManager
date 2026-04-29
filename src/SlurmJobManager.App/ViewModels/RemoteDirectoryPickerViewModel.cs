using System.Collections.ObjectModel;
using System.Windows.Input;
using SlurmJobManager.Core.Interfaces;

namespace SlurmJobManager.App.ViewModels;

/// <summary>
/// Backing view-model for the remote SSH directory picker dialog.
/// Displays a single-level list of sub-directories under the currently browsed path
/// and enforces a root constraint (users cannot navigate above <see cref="HomeDirectory"/>).
/// </summary>
public sealed class RemoteDirectoryPickerViewModel : ViewModelBase
{
    private readonly ISshClientService _ssh;

    private string _currentPath = string.Empty;
    private string? _selectedEntry;
    private bool _isBusy;
    private string _statusMessage = string.Empty;

    public string HomeDirectory { get; }

    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (SetField(ref _currentPath, value))
                OnPropertyChanged(nameof(CanGoUp));
        }
    }

    public string? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetField(ref _selectedEntry, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsBusy { get => _isBusy; private set => SetField(ref _isBusy, value); }
    public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }

    /// <summary>
    /// The full remote path the user has confirmed (set when the user clicks OK or double-clicks).
    /// <c>null</c> means the dialog was cancelled.
    /// </summary>
    public string? ResultPath { get; private set; }

    public ObservableCollection<string> Entries { get; } = new();

    /// <summary>True when navigation upward is possible (still within <see cref="HomeDirectory"/>).</summary>
    public bool CanGoUp => CurrentPath != HomeDirectory && CurrentPath.StartsWith(HomeDirectory, StringComparison.Ordinal);

    public ICommand NavigateIntoCommand { get; }
    public ICommand GoUpCommand         { get; }
    public ICommand RefreshCommand      { get; }
    public ICommand SelectCurrentCommand { get; }

    public RemoteDirectoryPickerViewModel(ISshClientService ssh, string homeDirectory)
    {
        _ssh          = ssh ?? throw new ArgumentNullException(nameof(ssh));
        HomeDirectory = homeDirectory.TrimEnd('/');
        _currentPath  = HomeDirectory;

        NavigateIntoCommand  = new AsyncRelayCommand<string>(NavigateIntoAsync);
        GoUpCommand          = new AsyncRelayCommand(GoUpAsync, () => CanGoUp && !IsBusy);
        RefreshCommand       = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        SelectCurrentCommand = new RelayCommand(SelectCurrent);
    }

    public async Task LoadInitialAsync(CancellationToken ct = default)
        => await LoadEntriesAsync(HomeDirectory, ct);

    private async Task NavigateIntoAsync(string? entry, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(entry)) return;
        var target = $"{CurrentPath.TrimEnd('/')}/{entry}";
        await LoadEntriesAsync(target, ct);
    }

    private async Task GoUpAsync(CancellationToken ct)
    {
        var parent = GetParent(CurrentPath);
        if (parent != null && parent.StartsWith(HomeDirectory, StringComparison.Ordinal))
            await LoadEntriesAsync(parent, ct);
    }

    private async Task RefreshAsync(CancellationToken ct)
        => await LoadEntriesAsync(CurrentPath, ct);

    private async Task LoadEntriesAsync(string path, CancellationToken ct)
    {
        IsBusy = true;
        StatusMessage = "加载中…";
        try
        {
            var dirs = await _ssh.ListDirectoriesAsync(path, ct);
            Entries.Clear();
            foreach (var d in dirs)
                Entries.Add(d);
            CurrentPath = path;
            SelectedEntry = null;
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"无法加载目录：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SelectCurrent()
    {
        ResultPath = SelectedEntry != null
            ? $"{CurrentPath.TrimEnd('/')}/{SelectedEntry}"
            : CurrentPath;
    }

    private static string? GetParent(string path)
    {
        var idx = path.TrimEnd('/').LastIndexOf('/');
        return idx > 0 ? path[..idx] : null;
    }
}
