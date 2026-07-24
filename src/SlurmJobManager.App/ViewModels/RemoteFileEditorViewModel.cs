using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using SlurmJobManager.App.Services;
using SlurmJobManager.Core.Interfaces;

namespace SlurmJobManager.App.ViewModels;

/// <summary>
/// Backing view-model for the remote SSH file editor dialog.
/// Loads a remote text file, lets the user edit it, and saves it back.
/// </summary>
public sealed class RemoteFileEditorViewModel : ViewModelBase
{
    private const long LargeFileThresholdBytes = 2 * 1024 * 1024;
    private const long VeryLargeFileThresholdBytes = 16 * 1024 * 1024;

    private readonly ISshClientService _ssh;
    private readonly AppPreferencesService _prefs;
    private readonly string _homeDirectory;
    private readonly IAppLogger? _logger;

    private string _content = string.Empty;
    private bool _isBusy;
    private string _statusMessage = string.Empty;
    private string _encodingName = "UTF-8";
    private bool _isBinaryFile;
    private bool _isDirty;
    private string _statusStyleKey = "InfoTextStyle";
    private string _lastSavedContent = string.Empty;
    private bool _suppressDirtyTracking;
    private long _fileSizeBytes;
    private bool _isLargeFileMode;
    private bool _isVeryLargeFileMode;
    private bool _isStructuredMode;
    private bool _isStructuredModeAvailable;
    private StructuredParameterFileFormat? _structuredFormat;
    private string _structuredRoundTripBaseline = string.Empty;
    private bool _saveCompleted;
    private bool _loadSucceeded;
    private TextEncodingDetectionResult _encodingDetection = new()
    {
        Encoding = new System.Text.UTF8Encoding(false),
        DisplayName = "UTF-8",
        HasBom = false,
        IsReliable = true,
        IsBinaryLike = false,
    };

    public string RemotePath { get; }
    public string DisplayRemotePath => RemotePathDisplayHelper.CollapseHomePath(RemotePath, _homeDirectory);
    public string FileName   => RemotePath.Contains('/') ? RemotePath[(RemotePath.LastIndexOf('/') + 1)..] : RemotePath;

    /// <summary>Formatted window title including the filename, resolved from localization resources at runtime.</summary>
    public string WindowTitle => $"{L("RemoteEditor.Title")} {FileName} - {RemotePath}";

    public string Content
    {
        get => _content;
        set
        {
            if (SetField(ref _content, value) && !_suppressDirtyTracking)
                IsDirty = !string.Equals(_content, _lastSavedContent, StringComparison.Ordinal);
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value))
                return;
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(CanReload));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }
    public string StatusStyleKey { get => _statusStyleKey; private set => SetField(ref _statusStyleKey, value); }
    public string EncodingName { get => _encodingName; private set => SetField(ref _encodingName, value); }
    public bool IsBinaryFile
    {
        get => _isBinaryFile;
        private set
        {
            if (!SetField(ref _isBinaryFile, value))
                return;
            OnPropertyChanged(nameof(CanSave));
        }
    }
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetField(ref _isDirty, value))
                OnPropertyChanged(nameof(EditorStateText));
        }
    }
    public long FileSizeBytes
    {
        get => _fileSizeBytes;
        private set
        {
            if (SetField(ref _fileSizeBytes, value))
                OnPropertyChanged(nameof(FileSizeText));
        }
    }

    public string FileSizeText => FormatFileSize(FileSizeBytes);
    public bool IsLargeFileMode { get => _isLargeFileMode; private set => SetField(ref _isLargeFileMode, value); }
    public bool IsVeryLargeFileMode { get => _isVeryLargeFileMode; private set => SetField(ref _isVeryLargeFileMode, value); }
    public string LargeFileModeText
    {
        get
        {
            if (IsVeryLargeFileMode)
                return string.Format(L("RemoteEditor.VeryLargeMode"), FileSizeText);
            if (IsLargeFileMode)
                return string.Format(L("RemoteEditor.LargeMode"), FileSizeText);
            return string.Empty;
        }
    }

    public bool IsStructuredMode
    {
        get => _isStructuredMode;
        private set
        {
            if (SetField(ref _isStructuredMode, value))
            {
                OnPropertyChanged(nameof(IsTextMode));
                OnPropertyChanged(nameof(EditorModeText));
                OnPropertyChanged(nameof(EditorStateText));
            }
        }
    }

    public bool IsTextMode => !IsStructuredMode;
    public string EditorModeText => IsStructuredMode ? L("RemoteEditor.StructuredMode") : L("RemoteEditor.TextMode");
    public string EditorStateText => string.Format(
        L("RemoteEditor.StateSummaryFormat"),
        EditorModeText,
        IsDirty ? L("RemoteEditor.StateDirty") : L("RemoteEditor.StateSaved"));

    public bool IsStructuredModeAvailable
    {
        get => _isStructuredModeAvailable;
        private set => SetField(ref _isStructuredModeAvailable, value);
    }

    /// <summary>Set to <c>true</c> after a successful save so the view can close.</summary>
    public bool SaveCompleted
    {
        get => _saveCompleted;
        private set => SetField(ref _saveCompleted, value);
    }

    public bool LoadSucceeded
    {
        get => _loadSucceeded;
        private set
        {
            if (!SetField(ref _loadSucceeded, value))
                return;
            OnPropertyChanged(nameof(CanSave));
        }
    }

    public bool CanSave => !IsBusy && LoadSucceeded && !IsBinaryFile;
    public bool CanReload => !IsBusy;

    public ObservableCollection<StructuredParameterItemViewModel> StructuredItems { get; } = new();

    public ICommand SaveCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand SwitchToTextModeCommand { get; }
    public ICommand SwitchToStructuredModeCommand { get; }

    public RemoteFileEditorViewModel(ISshClientService ssh, AppPreferencesService prefs, string remotePath, string? homeDirectory = null, IAppLogger? logger = null)
    {
        _ssh           = ssh ?? throw new ArgumentNullException(nameof(ssh));
        _prefs         = prefs ?? throw new ArgumentNullException(nameof(prefs));
        _homeDirectory = RemotePathDisplayHelper.NormalizeRemotePath(homeDirectory);
        _logger        = logger;
        RemotePath     = remotePath;
        SaveCommand    = new AsyncRelayCommand(SaveCommandAsync, () => !IsBusy);
        ReloadCommand = new AsyncRelayCommand(ReloadCommandAsync, () => !IsBusy);
        SwitchToTextModeCommand = new RelayCommand(SwitchToTextMode, () => IsStructuredModeAvailable);
        SwitchToStructuredModeCommand = new RelayCommand(SwitchToStructuredMode, () => IsStructuredModeAvailable);
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await LoadInternalAsync(preferStructuredModeAfterLoad: IsStructuredMode, ct);
    }

    private async Task LoadInternalAsync(bool preferStructuredModeAfterLoad, CancellationToken ct)
    {
        LoadSucceeded = false;
        SaveCompleted = false;
        IsBusy = true;
        SetStatus("RemoteEditor.ProbingFile", "InfoTextStyle");
        _logger?.Info($"Remote editor loading file. Path='{RemotePath}'.");
        try
        {
            FileSizeBytes = await _ssh.GetRemoteFileSizeAsync(RemotePath, ct);
            IsLargeFileMode = FileSizeBytes >= LargeFileThresholdBytes;
            IsVeryLargeFileMode = FileSizeBytes >= VeryLargeFileThresholdBytes;
            OnPropertyChanged(nameof(LargeFileModeText));

            if (IsVeryLargeFileMode)
            {
                var confirmText = string.Format(L("RemoteEditor.VeryLargeConfirm"), FileSizeText);
                var confirmTitle = L("RemoteEditor.VeryLargeTitle");
                var confirm = AppDialogService.ConfirmWarning(
                    confirmTitle,
                    confirmText,
                    confirmButtonText: L("Btn.Confirm"),
                    cancelButtonText: L("Btn.Cancel"));
                if (!confirm)
                {
                    SetStatus("RemoteEditor.OpenCancelled", "WarningTextStyle");
                    _logger?.Warning($"Remote editor open cancelled for very large file. Path='{RemotePath}'.");
                    return;
                }
            }

            SetStatus(IsLargeFileMode
                    ? string.Format(L("RemoteEditor.LargeLoadHint"), FileSizeText)
                    : L("RemoteEditor.Loading"),
                "InfoTextStyle",
                localize: false);

            var bytes = await _ssh.ReadFileBytesAsync(RemotePath, ct);
            _encodingDetection = TextEncodingDetector.Detect(bytes);
            EncodingName = _encodingDetection.DisplayName;

            if (_encodingDetection.IsBinaryLike)
            {
                IsBinaryFile = true;
                Content = string.Empty;
                _structuredRoundTripBaseline = string.Empty;
                StatusMessage = _encodingDetection.WarningMessage
                                ?? L("RemoteEditor.BinaryRejected");
                StatusStyleKey = "ErrorTextStyle";
                _logger?.Warning($"Remote editor blocked binary-like file. Path='{RemotePath}'.");
                return;
            }

            IsBinaryFile = false;
            _suppressDirtyTracking = true;
            Content = _encodingDetection.Encoding.GetString(bytes);
            _suppressDirtyTracking = false;
            _lastSavedContent = Content;
            IsDirty = false;
            _structuredRoundTripBaseline = Content;

            InitializeStructuredEditor(preferStructuredModeAfterLoad);

            if (!_encodingDetection.IsReliable)
            {
                StatusMessage = _encodingDetection.WarningMessage
                                ?? L("RemoteEditor.EncodingUnknown");
                StatusStyleKey = "WarningTextStyle";
            }
            else if (IsLargeFileMode)
            {
                SetStatus(string.Format(L("RemoteEditor.LargeModeReady"), FileSizeText), "WarningTextStyle", localize: false);
            }
            else
            {
                SetStatus(string.Empty, "InfoTextStyle");
            }

            LoadSucceeded = true;
            _logger?.Info($"Remote editor loaded file successfully. Path='{RemotePath}', Format='{_structuredFormat?.ToString() ?? "text-only"}'.");
        }
        catch (Exception ex)
        {
            SetStatus($"{L("RemoteEditor.LoadFailed")}{ex.Message}", "ErrorTextStyle", localize: false);
            _logger?.Error($"Remote editor failed to load file. Path='{RemotePath}'.", ex);
        }
        finally { IsBusy = false; }
    }

    public Task<bool> SaveChangesAsync(string? editorText = null, CancellationToken ct = default) => SaveAsync(editorText, ct);
    public async Task<bool> ReloadAsync(string? editorText = null, bool discardUnsavedChanges = false, CancellationToken ct = default)
    {
        if (IsBusy)
            return false;

        if (editorText != null && IsTextMode)
            Content = editorText;

        if (IsDirty && !discardUnsavedChanges)
        {
            SetStatus("RemoteEditor.ReloadNeedsConfirm", "WarningTextStyle");
            return false;
        }

        var preferredMode = IsStructuredMode;
        await LoadInternalAsync(preferredMode, ct);
        if (!LoadSucceeded)
            return false;

        SetStatus("RemoteEditor.Reloaded", "SuccessTextStyle");
        _logger?.Info($"Remote editor reloaded file. Path='{RemotePath}'.");
        return true;
    }

    private async Task SaveCommandAsync(CancellationToken ct)
        => await SaveAsync(editorText: null, ct);
    private async Task ReloadCommandAsync(CancellationToken ct)
        => await ReloadAsync(editorText: null, discardUnsavedChanges: true, ct);

    private async Task<bool> SaveAsync(string? editorText, CancellationToken ct)
    {
        if (IsBinaryFile)
        {
            SetStatus("RemoteEditor.BinaryRejected", "ErrorTextStyle");
            return false;
        }

        if (!LoadSucceeded)
        {
            SetStatus("RemoteEditor.SaveBeforeLoadBlocked", "ErrorTextStyle");
            _logger?.Warning($"Remote editor blocked save before successful load. Path='{RemotePath}'.");
            return false;
        }

        string contentToSave;
        string saveSource;

        if (IsStructuredModeAvailable && IsStructuredMode)
        {
            if (!TryBuildStructuredContent(out var structuredContent, out var error))
            {
                SetStatus(error, "ErrorTextStyle", localize: false);
                _logger?.Warning($"Remote editor structured serialization failed. Path='{RemotePath}', Error='{error}'.");
                return false;
            }

            _suppressDirtyTracking = true;
            Content = structuredContent;
            _suppressDirtyTracking = false;
            contentToSave = structuredContent;
            saveSource = "structured";
        }
        else
        {
            if (editorText != null)
                Content = editorText;
            contentToSave = Content ?? string.Empty;
            saveSource = "text";
        }

        if (contentToSave.Length == 0 && FileSizeBytes > 0)
        {
            var confirm = AppDialogService.ConfirmWarning(
                L("RemoteEditor.EmptyOverwriteTitle"),
                string.Format(L("RemoteEditor.EmptyOverwriteConfirm"), FileSizeText),
                confirmButtonText: L("Btn.Confirm"),
                cancelButtonText: L("Btn.Cancel"));
            if (!confirm)
            {
                SetStatus("RemoteEditor.SaveCancelled", "WarningTextStyle");
                _logger?.Warning($"Remote editor cancelled empty overwrite save. Path='{RemotePath}'.");
                return false;
            }
        }

        if (IsVeryLargeFileMode)
        {
            var confirmText = string.Format(L("RemoteEditor.VeryLargeSaveConfirm"), FileSizeText);
            var confirm = AppDialogService.ConfirmWarning(
                L("RemoteEditor.VeryLargeSaveTitle"),
                confirmText,
                confirmButtonText: L("Btn.Confirm"),
                cancelButtonText: L("Btn.Cancel"));
            if (!confirm)
            {
                SetStatus("RemoteEditor.SaveCancelled", "WarningTextStyle");
                _logger?.Warning($"Remote editor cancelled very large file save. Path='{RemotePath}'.");
                return false;
            }
        }

        IsBusy = true;
        SetStatus(IsLargeFileMode ? "RemoteEditor.SavingLarge" : "RemoteEditor.Saving", "InfoTextStyle");
        _logger?.Info($"Remote editor saving file. Path='{RemotePath}', Source='{saveSource}', Length={contentToSave.Length}.");
        try
        {
            // Normalize to Unix line endings (LF only) before writing to the remote Linux file
            // system. Content typed or pasted on Windows may contain CRLF (\r\n). Standalone
            // CR (\r) from Mac Classic line endings is also normalized for completeness.
            var normalizedContent = contentToSave.Replace("\r\n", "\n").Replace("\r", "\n");
            var bytes = TextEncodingDetector.Encode(normalizedContent, _encodingDetection);
            await _ssh.WriteFileBytesAsync(RemotePath, bytes, ct);
            _suppressDirtyTracking = true;
            Content = normalizedContent;
            _suppressDirtyTracking = false;
            _lastSavedContent = normalizedContent;
            _structuredRoundTripBaseline = normalizedContent;
            IsDirty = false;
            ReinitializeStructuredStateFromContent(preferCurrentMode: IsStructuredMode);
            SetStatus($"{L("RemoteEditor.Saved")}{DateTime.Now:HH:mm:ss}", "SuccessTextStyle", localize: false);
            SaveCompleted = true;
            _logger?.Info($"Remote editor saved file successfully. Path='{RemotePath}', Source='{saveSource}' (line endings normalized to LF).");
            return true;
        }
        catch (Exception ex)
        {
            SetStatus($"{L("RemoteEditor.SaveFailed")}{ex.Message}", "ErrorTextStyle", localize: false);
            _logger?.Error($"Remote editor save failed. Path='{RemotePath}', Source='{saveSource}'.", ex);
            return false;
        }
        finally { IsBusy = false; }
    }

    private void InitializeStructuredEditor(bool preferStructuredModeAfterLoad)
    {
        foreach (var item in StructuredItems)
            item.PropertyChanged -= OnStructuredItemPropertyChanged;
        StructuredItems.Clear();
        _structuredFormat = null;

        if (!StructuredParameterParser.TryParse(RemotePath, Content, out var format, out var entries))
        {
            IsStructuredModeAvailable = false;
            SetStructuredMode(false);
            CommandManager.InvalidateRequerySuggested();
            return;
        }

        _structuredFormat = format;
        foreach (var entry in entries)
        {
            var item = new StructuredParameterItemViewModel(entry, BrowseRemotePathAsync);
            item.PropertyChanged += OnStructuredItemPropertyChanged;
            StructuredItems.Add(item);
        }

        IsStructuredModeAvailable = StructuredItems.Count > 0;
        SetStructuredMode(IsStructuredModeAvailable && preferStructuredModeAfterLoad);
        CommandManager.InvalidateRequerySuggested();
    }

    private bool TryBuildStructuredContent(out string content, out string error)
    {
        content = string.Empty;
        error = L("RemoteEditor.StructuredSaveFailed");

        if (!IsStructuredModeAvailable || _structuredFormat == null)
        {
            error = L("RemoteEditor.StructuredUnavailable");
            return false;
        }

        try
        {
            var entries = StructuredItems.Select(i => i.ToEntry()).ToList();
            content = StructuredParameterParser.Serialize(_structuredFormat.Value, entries, _structuredRoundTripBaseline);
            return true;
        }
        catch (Exception ex)
        {
            error = string.Format(L("RemoteEditor.StructuredSaveFailedFormat"), ex.Message);
            return false;
        }
    }

    private async Task<string?> BrowseRemotePathAsync(StructuredParameterItemViewModel item, CancellationToken ct)
    {
        if (!IsSshConnectedSafe())
        {
            SetStatus("Task.RequireConnectionForBrowse", "WarningTextStyle");
            return null;
        }

        var startDir = ResolveRemotePickerStartDirectory();

        if (item.ShouldPreferDirectoryPicker)
        {
            var vm = new RemoteDirectoryPickerViewModel(_ssh, startDir, _homeDirectory);
            var win = new Views.RemoteDirectoryPickerView { DataContext = vm };
            if (Application.Current.MainWindow is { } mainWin) win.Owner = mainWin;
            await vm.LoadInitialAsync(ct);
            return win.ShowDialog() == true ? vm.ResultPath : null;
        }

        var fileVm = new RemoteFilePickerViewModel(_ssh, startDir);
        var fileWin = new Views.RemoteFilePickerView { DataContext = fileVm };
        if (Application.Current.MainWindow is { } owner) fileWin.Owner = owner;
        await fileVm.LoadInitialAsync(ct);
        return fileWin.ShowDialog() == true ? fileVm.ResultPath : null;
    }

    private string ResolveRemotePickerStartDirectory()
    {
        var configured = _prefs.DefaultRemotePickerDirectory;
        if (!string.IsNullOrWhiteSpace(configured))
            return RemotePathDisplayHelper.ExpandHomePath(configured, _homeDirectory);

        return AppPreferencesService.DefaultRemotePickerDirectoryFallback;
    }

    private bool IsSshConnectedSafe()
    {
        try
        {
            return _ssh.IsConnected;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void SetStructuredMode(bool enable)
    {
        IsStructuredMode = IsStructuredModeAvailable && enable;
    }

    private void SwitchToTextMode()
    {
        if (!IsStructuredModeAvailable)
            return;

        var error = string.Empty;
        if (IsStructuredMode && TryBuildStructuredContent(out var structuredContent, out error))
        {
            _suppressDirtyTracking = true;
            Content = structuredContent;
            _suppressDirtyTracking = false;
            IsDirty = !string.Equals(Content, _lastSavedContent, StringComparison.Ordinal);
            _structuredRoundTripBaseline = Content;
        }
        else if (IsStructuredMode)
        {
            SetStatus(error, "ErrorTextStyle", localize: false);
            return;
        }

        SetStructuredMode(false);
        SetStatus("RemoteEditor.TextModeSynced", "InfoTextStyle");
        _logger?.Info($"Remote editor switched to text mode. Path='{RemotePath}'.");
    }

    private void SwitchToStructuredMode()
    {
        if (!IsStructuredModeAvailable)
            return;

        if (!TryReparseStructuredFromCurrentText(out var error))
        {
            SetStatus(error, "WarningTextStyle", localize: false);
            _logger?.Warning($"Remote editor failed to switch to structured mode from current text. Path='{RemotePath}', Error='{error}'.");
            return;
        }

        SetStructuredMode(true);
        SetStatus("RemoteEditor.StructuredModeSynced", "InfoTextStyle");
        _logger?.Info($"Remote editor switched to structured mode. Path='{RemotePath}'.");
    }

    private bool TryReparseStructuredFromCurrentText(out string error)
    {
        error = string.Empty;
        var text = Content ?? string.Empty;
        if (!StructuredParameterParser.TryParse(RemotePath, text, out var format, out var entries))
        {
            error = L("RemoteEditor.StructuredParseFromTextFailed");
            return false;
        }

        foreach (var item in StructuredItems)
            item.PropertyChanged -= OnStructuredItemPropertyChanged;
        StructuredItems.Clear();
        _structuredFormat = format;
        foreach (var entry in entries)
        {
            var item = new StructuredParameterItemViewModel(entry, BrowseRemotePathAsync);
            item.PropertyChanged += OnStructuredItemPropertyChanged;
            StructuredItems.Add(item);
        }

        IsStructuredModeAvailable = StructuredItems.Count > 0;
        _structuredRoundTripBaseline = text;
        CommandManager.InvalidateRequerySuggested();
        return IsStructuredModeAvailable;
    }

    private void ReinitializeStructuredStateFromContent(bool preferCurrentMode)
    {
        var previousMode = preferCurrentMode && IsStructuredMode;
        InitializeStructuredEditor(previousMode);
    }

    private static string L(string key)
        => Application.Current?.TryFindResource(key) as string ?? key;

    private void OnStructuredItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StructuredParameterItemViewModel.StringValue)
            or nameof(StructuredParameterItemViewModel.BoolValue)
            or nameof(StructuredParameterItemViewModel.IntegerValue)
            or nameof(StructuredParameterItemViewModel.FloatingValue)
            or nameof(StructuredParameterItemViewModel.ValueKind))
        {
            IsDirty = true;
        }
    }

    private void SetStatus(string messageOrKey, string styleKey, bool localize = true)
    {
        StatusStyleKey = styleKey;
        StatusMessage = string.IsNullOrEmpty(messageOrKey)
            ? string.Empty
            : (localize ? L(messageOrKey) : messageOrKey);
    }

    private static string FormatFileSize(long bytes)
    {
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var unitIndex = 0;
        double value = bytes;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024d;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }
}

public sealed class StructuredParameterItemViewModel : ViewModelBase
{
    private readonly Func<StructuredParameterItemViewModel, CancellationToken, Task<string?>> _browseRemotePathAsync;

    private StructuredParameterValueKind _valueKind;
    private string _stringValue;
    private bool _boolValue;
    private long _integerValue;
    private double _floatingValue;

    public StructuredParameterItemViewModel(
        StructuredParameterEntry entry,
        Func<StructuredParameterItemViewModel, CancellationToken, Task<string?>> browseRemotePathAsync)
    {
        Section = entry.Section;
        Key = entry.Key;
        JsonPathSegments = entry.JsonPathSegments;
        _valueKind = entry.ValueKind;
        _stringValue = entry.StringValue;
        _boolValue = entry.BoolValue;
        _integerValue = entry.IntegerValue;
        _floatingValue = entry.FloatingValue;
        _browseRemotePathAsync = browseRemotePathAsync;

        BrowseValueCommand = new AsyncRelayCommand(BrowseValueAsync, () => ValueKind == StructuredParameterValueKind.String);
    }

    public string Section { get; }
    public string Key { get; }
    public string[] JsonPathSegments { get; }

    public StructuredParameterValueKind ValueKind
    {
        get => _valueKind;
        set
        {
            if (SetField(ref _valueKind, value))
            {
                OnPropertyChanged(nameof(IsString));
                OnPropertyChanged(nameof(IsBoolean));
                OnPropertyChanged(nameof(IsInteger));
                OnPropertyChanged(nameof(IsFloating));
                OnPropertyChanged(nameof(IsNull));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string StringValue
    {
        get => _stringValue;
        set => SetField(ref _stringValue, value);
    }

    public bool BoolValue
    {
        get => _boolValue;
        set => SetField(ref _boolValue, value);
    }

    public long IntegerValue
    {
        get => _integerValue;
        set => SetField(ref _integerValue, value);
    }

    public double FloatingValue
    {
        get => _floatingValue;
        set => SetField(ref _floatingValue, value);
    }

    public bool IsString => ValueKind == StructuredParameterValueKind.String;
    public bool IsBoolean => ValueKind == StructuredParameterValueKind.Boolean;
    public bool IsInteger => ValueKind == StructuredParameterValueKind.Integer;
    public bool IsFloating => ValueKind == StructuredParameterValueKind.Floating;
    public bool IsNull => ValueKind == StructuredParameterValueKind.Null;

    public bool ShouldPreferDirectoryPicker
    {
        get
        {
            var lower = Key.ToLowerInvariant();
            return lower.Contains("dir")
                   || lower.Contains("directory")
                   || lower.Contains("folder");
        }
    }

    public ICommand BrowseValueCommand { get; }

    public StructuredParameterEntry ToEntry()
        => new(
            Section,
            Key,
            JsonPathSegments,
            ValueKind,
            StringValue,
            BoolValue,
            IntegerValue,
            FloatingValue);

    private async Task BrowseValueAsync(CancellationToken ct)
    {
        var selected = await _browseRemotePathAsync(this, ct);
        if (!string.IsNullOrWhiteSpace(selected))
            StringValue = selected;
    }
}
