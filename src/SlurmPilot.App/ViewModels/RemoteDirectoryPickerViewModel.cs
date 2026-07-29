using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using SlurmPilot.App.Services;
using SlurmPilot.Core.Interfaces;

namespace SlurmPilot.App.ViewModels;

/// <summary>
/// Backing view-model for the remote SSH directory picker dialog.
/// Displays a single-level list of sub-directories under the currently browsed path
/// and enforces a root constraint (users cannot navigate above <see cref="HomeDirectory"/>).
/// </summary>
public sealed class RemoteDirectoryPickerViewModel : ViewModelBase
{
    private readonly ISshClientService _ssh;
    private readonly string _homeDirectory;

    private string _currentPath = string.Empty;
    private string _pathInput = string.Empty;
    private string? _selectedEntry;
    private bool _isBusy;
    private string _statusMessage = string.Empty;
    private string _statusStyleKey = "InfoTextStyle";

    public string HomeDirectory => _homeDirectory;

    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (SetField(ref _currentPath, value))
            {
                PathInput = value;
                OnPropertyChanged(nameof(CanGoUp));
            }
        }
    }

    public string PathInput
    {
        get => _pathInput;
        set
        {
            if (SetField(ref _pathInput, value))
                CommandManager.InvalidateRequerySuggested();
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
    public string StatusStyleKey { get => _statusStyleKey; private set => SetField(ref _statusStyleKey, value); }

    /// <summary>
    /// The full remote path the user has confirmed (set when the user clicks OK or double-clicks).
    /// <c>null</c> means the dialog was cancelled.
    /// </summary>
    public string? ResultPath { get; private set; }

    public ObservableCollection<string> Entries { get; } = new();
    public ObservableCollection<string> QuickPaths { get; } = new();

    public bool CanGoUp => GetParent(CurrentPath) != null;

    public ICommand NavigateIntoCommand { get; }
    public ICommand GoUpCommand         { get; }
    public ICommand GoToPathCommand     { get; }
    public ICommand RefreshCommand      { get; }
    public ICommand SelectCurrentCommand { get; }

    public RemoteDirectoryPickerViewModel(ISshClientService ssh, string initialDirectory, string? homeDirectory = null)
    {
        _ssh          = ssh ?? throw new ArgumentNullException(nameof(ssh));
        _homeDirectory = NormalizeDirectory(string.IsNullOrWhiteSpace(homeDirectory) ? initialDirectory : homeDirectory);
        _currentPath  = NormalizeDirectory(ExpandHomePath(initialDirectory));
        _pathInput = _currentPath;
        SeedQuickPaths(_currentPath);

        NavigateIntoCommand  = new AsyncRelayCommand<string>(NavigateIntoAsync);
        GoUpCommand          = new AsyncRelayCommand(GoUpAsync, () => CanGoUp && !IsBusy);
        GoToPathCommand      = new AsyncRelayCommand(GoToPathAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(PathInput));
        RefreshCommand       = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        SelectCurrentCommand = new RelayCommand(SelectCurrent);
    }

    public async Task LoadInitialAsync(CancellationToken ct = default)
        => await LoadEntriesAsync(HomeDirectory, ct);

    private async Task NavigateIntoAsync(string? entry, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(entry)) return;
        var target = NormalizeDirectory($"{CurrentPath.TrimEnd('/')}/{entry}");
        await LoadEntriesAsync(target, ct);
    }

    private async Task GoUpAsync(CancellationToken ct)
    {
        var parent = GetParent(CurrentPath);
        if (parent != null)
            await LoadEntriesAsync(parent, ct);
    }

    private async Task GoToPathAsync(CancellationToken ct)
    {
        var target = NormalizeDirectory(ExpandHomePath(PathInput));
        if (string.IsNullOrWhiteSpace(target))
            return;

        await LoadEntriesAsync(target, ct);
    }

    private async Task RefreshAsync(CancellationToken ct)
        => await LoadEntriesAsync(CurrentPath, ct);

    private async Task LoadEntriesAsync(string path, CancellationToken ct)
    {
        IsBusy = true;
        SetStatus("RemotePicker.StatusLoading", "InfoTextStyle");
        try
        {
            var dirs = await _ssh.ListDirectoriesAsync(path, ct);
            Entries.Clear();
            foreach (var d in dirs)
                Entries.Add(d);
            CurrentPath = NormalizeDirectory(path);
            AddQuickPath(CurrentPath);
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

    private void SelectCurrent()
    {
        ResultPath = SelectedEntry != null
            ? NormalizeDirectory($"{CurrentPath.TrimEnd('/')}/{SelectedEntry}")
            : CurrentPath;
    }

    public async Task<bool> TrySelectCurrentAsync(CancellationToken ct = default)
    {
        SelectCurrent();
        if (string.IsNullOrWhiteSpace(ResultPath))
            return false;

        var access = await CheckDirectoryReadWriteAsync(ResultPath, ct);
        if (access == RemoteDirectoryAccessResult.ReadWrite)
            return true;

        ResultPath = null;
        SetStatus(access switch
        {
            RemoteDirectoryAccessResult.NotFound => L("RemotePicker.AccessNotFound"),
            RemoteDirectoryAccessResult.NotReadable => L("RemotePicker.AccessNotReadable"),
            RemoteDirectoryAccessResult.NotWritable => L("RemotePicker.AccessNotWritable"),
            _ => L("RemotePicker.AccessCheckFailed"),
        }, "ErrorTextStyle", localize: false);
        return false;
    }

    private static string? GetParent(string path)
    {
        var trimmed = NormalizeDirectory(path);
        if (trimmed == "/")
            return null;

        var idx = trimmed.LastIndexOf('/');
        if (idx > 0) return trimmed[..idx];
        return "/";
    }

    private async Task<RemoteDirectoryAccessResult> CheckDirectoryReadWriteAsync(string path, CancellationToken ct)
    {
        try
        {
            var escaped = EscapeShellArg(NormalizeDirectory(ExpandHomePath(path)));
            var (stdout, _, exitCode) = await _ssh.ExecuteAsync(
                $"if [ ! -d {escaped} ]; then echo missing; elif [ ! -r {escaped} ]; then echo unreadable; elif [ ! -w {escaped} ]; then echo unwritable; else echo ok; fi",
                ct);
            if (exitCode != 0)
                return RemoteDirectoryAccessResult.CheckFailed;

            return stdout.Trim() switch
            {
                "ok" => RemoteDirectoryAccessResult.ReadWrite,
                "missing" => RemoteDirectoryAccessResult.NotFound,
                "unreadable" => RemoteDirectoryAccessResult.NotReadable,
                "unwritable" => RemoteDirectoryAccessResult.NotWritable,
                _ => RemoteDirectoryAccessResult.CheckFailed,
            };
        }
        catch
        {
            return RemoteDirectoryAccessResult.CheckFailed;
        }
    }

    private void SeedQuickPaths(string currentPath)
    {
        AddQuickPath(currentPath);
        AddQuickPath(_homeDirectory);
        AddQuickPath("~/");
        AddQuickPath("/gpfs");
        AddQuickPath("/");
    }

    private void AddQuickPath(string? path)
    {
        var normalized = NormalizeDirectory(path);
        if (string.IsNullOrWhiteSpace(normalized))
            return;
        if (!QuickPaths.Contains(normalized))
            QuickPaths.Add(normalized);
    }

    private string ExpandHomePath(string? path)
        => RemotePathDisplayHelper.ExpandHomePath(path, _homeDirectory);

    private static string NormalizeDirectory(string? path)
    {
        var normalized = RemotePathDisplayHelper.NormalizeRemotePath(path);
        if (normalized == "~")
            return "~/";
        return normalized;
    }

    private static string EscapeShellArg(string arg)
        => "'" + arg.Replace("'", "'\\''") + "'";

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

public enum RemoteDirectoryAccessResult
{
    ReadWrite,
    NotFound,
    NotReadable,
    NotWritable,
    CheckFailed,
}
