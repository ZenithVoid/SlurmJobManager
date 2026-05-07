using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using SlurmJobManager.Core.Interfaces;

namespace SlurmJobManager.App.ViewModels;

public sealed class RemoteFilePickerViewModel : ViewModelBase
{
    private readonly ISshClientService _ssh;

    private string _currentPath = string.Empty;
    private RemoteFilePickerEntry? _selectedEntry;
    private bool _isBusy;
    private string _statusMessage = string.Empty;
    private string _statusStyleKey = "InfoTextStyle";

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

    public RemoteFilePickerEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetField(ref _selectedEntry, value))
            {
                OnPropertyChanged(nameof(CanSelectFile));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsBusy { get => _isBusy; private set => SetField(ref _isBusy, value); }
    public string StatusMessage { get => _statusMessage; private set => SetField(ref _statusMessage, value); }
    public string StatusStyleKey { get => _statusStyleKey; private set => SetField(ref _statusStyleKey, value); }

    public bool CanGoUp
    {
        get
        {
            var parent = GetParent(CurrentPath);
            return !string.IsNullOrWhiteSpace(parent) && IsWithinHomeScope(parent);
        }
    }
    public bool CanSelectFile => SelectedEntry is { IsDirectory: false };

    public string? ResultPath { get; private set; }

    public ObservableCollection<RemoteFilePickerEntry> Entries { get; } = new();

    public ICommand NavigateIntoCommand { get; }
    public ICommand GoUpCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SelectFileCommand { get; }

    public RemoteFilePickerViewModel(ISshClientService ssh, string homeDirectory)
    {
        _ssh = ssh ?? throw new ArgumentNullException(nameof(ssh));
        HomeDirectory = string.IsNullOrWhiteSpace(homeDirectory) ? "~" : homeDirectory.TrimEnd('/');
        _currentPath = HomeDirectory;

        NavigateIntoCommand = new AsyncRelayCommand<RemoteFilePickerEntry>(NavigateIntoAsync);
        GoUpCommand = new AsyncRelayCommand(GoUpAsync, () => !IsBusy && CanGoUp);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        SelectFileCommand = new RelayCommand(SelectFile, () => CanSelectFile);
    }

    public async Task LoadInitialAsync(CancellationToken ct = default)
        => await LoadEntriesAsync(HomeDirectory, ct);

    private async Task NavigateIntoAsync(RemoteFilePickerEntry? entry, CancellationToken ct)
    {
        if (entry is null || !entry.IsDirectory) return;
        await LoadEntriesAsync(entry.FullPath, ct);
    }

    private async Task GoUpAsync(CancellationToken ct)
    {
        var parent = GetParent(CurrentPath);
        if (!string.IsNullOrWhiteSpace(parent) && IsWithinHomeScope(parent))
            await LoadEntriesAsync(parent, ct);
    }

    private async Task RefreshAsync(CancellationToken ct)
        => await LoadEntriesAsync(CurrentPath, ct);

    private async Task LoadEntriesAsync(string path, CancellationToken ct)
    {
        IsBusy = true;
        SetStatus("RemotePicker.StatusLoading", "InfoTextStyle");
        try
        {
            var directoriesTask = _ssh.ListDirectoriesAsync(path, ct);
            var filesTask = _ssh.ListFilesAsync(path, ct);
            await Task.WhenAll(directoriesTask, filesTask);

            var dirs = directoriesTask.Result
                .OrderBy(x => x, StringComparer.Ordinal)
                .Select(name => new RemoteFilePickerEntry(path, name, isDirectory: true));
            var files = filesTask.Result
                .OrderBy(x => x, StringComparer.Ordinal)
                .Select(name => new RemoteFilePickerEntry(path, name, isDirectory: false));

            Entries.Clear();
            foreach (var entry in dirs.Concat(files))
                Entries.Add(entry);

            CurrentPath = path;
            SelectedEntry = null;
            SetStatus(string.Empty, "InfoTextStyle");
        }
        catch (Exception ex)
        {
            SetStatus(string.Format(L("RemotePicker.StatusLoadFailed"), ex.Message), "ErrorTextStyle", localize: false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SelectFile()
    {
        if (SelectedEntry is not { IsDirectory: false }) return;
        ResultPath = SelectedEntry.FullPath;
    }

    private static string? GetParent(string path)
    {
        var trimmed = path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        if (idx > 0) return trimmed[..idx];
        if (idx == 0) return "/";
        return null;
    }

    private bool IsWithinHomeScope(string path)
    {
        if (HomeDirectory == "~")
            return true;
        return path.StartsWith(HomeDirectory, StringComparison.Ordinal);
    }

    private static string L(string key)
        => Application.Current?.TryFindResource(key) as string ?? key;

    private void SetStatus(string messageOrKey, string styleKey, bool localize = true)
    {
        StatusStyleKey = styleKey;
        StatusMessage = string.IsNullOrEmpty(messageOrKey)
            ? string.Empty
            : (localize ? L(messageOrKey) : messageOrKey);
    }
}

public sealed class RemoteFilePickerEntry
{
    public string Name { get; }
    public bool IsDirectory { get; }
    public string FullPath { get; }

    public string Icon => IsDirectory ? "📁" : "📄";

    public RemoteFilePickerEntry(string parentPath, string name, bool isDirectory)
    {
        Name = name;
        IsDirectory = isDirectory;
        FullPath = $"{parentPath.TrimEnd('/')}/{name}";
    }
}
