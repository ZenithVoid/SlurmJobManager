using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using SlurmJobManager.App.Services;

namespace SlurmJobManager.App.ViewModels.Dialogs;

public sealed class TaskPathPickerViewModel : ViewModelBase
{
    private readonly TaskPathKind _kind;
    private readonly TaskPathLibraryService _library;
    private readonly List<string> _candidatePaths;
    private string _searchText = string.Empty;
    private string _manualPath = string.Empty;
    private TaskPathPickerItem? _selectedItem;

    public ObservableCollection<TaskPathPickerItem> Items { get; } = new();
    public string ConfirmedPath { get; private set; } = string.Empty;
    public bool BrowseRequested { get; private set; }

    public string Title => _kind == TaskPathKind.Program
        ? L("TaskPathPicker.ProgramTitle", "选择程序")
        : L("TaskPathPicker.ParameterTitle", "选择配置文件");

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetField(ref _searchText, value))
                RebuildItems();
        }
    }

    public string ManualPath
    {
        get => _manualPath;
        set
        {
            if (SetField(ref _manualPath, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public TaskPathPickerItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetField(ref _selectedItem, value))
                return;

            if (value != null)
                ManualPath = value.Path;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public ICommand ToggleFavoriteCommand { get; }
    public ICommand SelectCommand { get; }
    public ICommand UseManualCommand { get; }
    public ICommand BrowseRemoteCommand { get; }

    public TaskPathPickerViewModel(
        TaskPathKind kind,
        IEnumerable<string> candidatePaths,
        string? currentPath,
        TaskPathLibraryService library)
    {
        _kind = kind;
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _candidatePaths = candidatePaths
            .Select(NormalizePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        ManualPath = NormalizePath(currentPath);

        ToggleFavoriteCommand = new RelayCommand<TaskPathPickerItem>(ToggleFavorite, item => item != null);
        SelectCommand = new RelayCommand(() => ConfirmSelected(), () => SelectedItem != null);
        UseManualCommand = new RelayCommand(() => ConfirmManual(), () => !string.IsNullOrWhiteSpace(ManualPath));
        BrowseRemoteCommand = new RelayCommand(() => RequestBrowse());

        RebuildItems();
        SelectedItem = Items.FirstOrDefault(x => string.Equals(x.Path, ManualPath, StringComparison.Ordinal))
            ?? Items.FirstOrDefault();
    }

    public bool ConfirmSelected()
    {
        if (SelectedItem == null)
            return false;

        ConfirmedPath = SelectedItem.Path;
        _library.Remember(_kind, ConfirmedPath);
        return true;
    }

    public bool ConfirmManual()
    {
        var path = NormalizePath(ManualPath);
        if (string.IsNullOrWhiteSpace(path))
            return false;

        ConfirmedPath = path;
        _library.Remember(_kind, ConfirmedPath);
        return true;
    }

    public bool RequestBrowse()
    {
        BrowseRequested = true;
        return true;
    }

    private void ToggleFavorite(TaskPathPickerItem? item)
    {
        if (item == null)
            return;

        item.IsFavorite = _library.ToggleFavorite(_kind, item.Path);
        item.SourceText = ResolveSourceText(item.Path, item.IsFavorite);
    }

    private void RebuildItems()
    {
        var selectedPath = SelectedItem?.Path;
        var lower = SearchText.Trim().ToLowerInvariant();
        var merged = new Dictionary<string, TaskPathPickerItem>(StringComparer.Ordinal);

        foreach (var entry in _library.GetEntries(_kind))
        {
            if (string.IsNullOrWhiteSpace(entry.Path))
                continue;

            merged[entry.Path] = new TaskPathPickerItem(
                entry.Path,
                entry.IsFavorite
                    ? L("TaskPathPicker.SourceFavorite", "收藏")
                    : L("TaskPathPicker.SourceRecent", "历史"),
                entry.IsFavorite,
                entry.LastUsedAtUtc);
        }

        foreach (var path in _candidatePaths)
        {
            if (merged.ContainsKey(path))
                continue;

            merged[path] = new TaskPathPickerItem(
                path,
                L("TaskPathPicker.SourceCandidate", "扫描结果"),
                _library.IsFavorite(_kind, path),
                DateTime.MinValue);
        }

        var filtered = merged.Values
            .Where(x => string.IsNullOrEmpty(lower) || x.Path.ToLowerInvariant().Contains(lower))
            .OrderByDescending(x => x.IsFavorite)
            .ThenByDescending(x => x.LastUsedAtUtc)
            .ThenBy(x => x.Path, StringComparer.Ordinal)
            .ToList();

        Items.Clear();
        foreach (var item in filtered)
            Items.Add(item);

        SelectedItem = Items.FirstOrDefault(x => string.Equals(x.Path, selectedPath, StringComparison.Ordinal))
            ?? Items.FirstOrDefault();
    }

    private string ResolveSourceText(string path, bool isFavorite)
    {
        if (isFavorite)
            return L("TaskPathPicker.SourceFavorite", "收藏");

        var hasHistory = _library.GetEntries(_kind).Any(entry => string.Equals(entry.Path, path, StringComparison.Ordinal));
        if (hasHistory)
            return L("TaskPathPicker.SourceRecent", "历史");

        return _candidatePaths.Contains(path, StringComparer.Ordinal)
            ? L("TaskPathPicker.SourceCandidate", "扫描结果")
            : L("TaskPathPicker.SourceRecent", "历史");
    }

    private static string NormalizePath(string? path)
        => (path ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');

    private static string L(string key, string fallback)
        => Application.Current?.TryFindResource(key) as string ?? fallback;
}

public sealed class TaskPathPickerItem : ViewModelBase
{
    private bool _isFavorite;
    private string _sourceText;

    public string Path { get; }
    public string DisplayName { get; }
    public DateTime LastUsedAtUtc { get; }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (SetField(ref _isFavorite, value))
                OnPropertyChanged(nameof(FavoriteText));
        }
    }

    public string FavoriteText
        => IsFavorite
            ? TaskPathPickerViewModelResource.L("TaskPathPicker.Unfavorite", "取消收藏")
            : TaskPathPickerViewModelResource.L("TaskPathPicker.Favorite", "收藏");

    public string SourceText
    {
        get => _sourceText;
        set => SetField(ref _sourceText, value);
    }

    public TaskPathPickerItem(string path, string sourceText, bool isFavorite, DateTime lastUsedAtUtc)
    {
        Path = path;
        DisplayName = GetRemoteFileName(path);
        _sourceText = sourceText;
        _isFavorite = isFavorite;
        LastUsedAtUtc = lastUsedAtUtc;
    }

    private static string GetRemoteFileName(string path)
    {
        var normalized = (path ?? string.Empty).TrimEnd('/');
        var idx = normalized.LastIndexOf('/');
        return idx >= 0 && idx < normalized.Length - 1 ? normalized[(idx + 1)..] : normalized;
    }
}

internal static class TaskPathPickerViewModelResource
{
    public static string L(string key, string fallback)
        => Application.Current?.TryFindResource(key) as string ?? fallback;
}
