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
    private readonly string _homeDirectory;

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

    public bool IsBusy { get => _isBusy; private set => SetField(ref _isBusy, value); }
    public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }
    public string StatusStyleKey { get => _statusStyleKey; private set => SetField(ref _statusStyleKey, value); }
    public string EncodingName { get => _encodingName; private set => SetField(ref _encodingName, value); }
    public bool IsBinaryFile { get => _isBinaryFile; private set => SetField(ref _isBinaryFile, value); }
    public bool IsDirty { get => _isDirty; private set => SetField(ref _isDirty, value); }
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

    /// <summary>Set to <c>true</c> after a successful save so the view can close.</summary>
    public bool SaveCompleted { get; private set; }
    public bool LoadSucceeded { get; private set; }

    public ICommand SaveCommand { get; }

    public RemoteFileEditorViewModel(ISshClientService ssh, string remotePath, string? homeDirectory = null)
    {
        _ssh           = ssh ?? throw new ArgumentNullException(nameof(ssh));
        _homeDirectory = RemotePathDisplayHelper.NormalizeRemotePath(homeDirectory);
        RemotePath     = remotePath;
        SaveCommand    = new AsyncRelayCommand<string>(SaveFromEditorAsync, _ => !IsBusy);
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        LoadSucceeded = false;
        IsBusy = true;
        SetStatus("RemoteEditor.ProbingFile", "InfoTextStyle");
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
                var confirm = MessageBox.Show(
                    confirmText,
                    confirmTitle,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes)
                {
                    SetStatus("RemoteEditor.OpenCancelled", "WarningTextStyle");
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
                StatusMessage = _encodingDetection.WarningMessage
                                ?? L("RemoteEditor.BinaryRejected");
                StatusStyleKey = "ErrorTextStyle";
                return;
            }

            IsBinaryFile = false;
            _suppressDirtyTracking = true;
            Content = _encodingDetection.Encoding.GetString(bytes);
            _suppressDirtyTracking = false;
            _lastSavedContent = Content;
            IsDirty = false;
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
        }
        catch (Exception ex)
        {
            SetStatus($"{L("RemoteEditor.LoadFailed")}{ex.Message}", "ErrorTextStyle", localize: false);
        }
        finally { IsBusy = false; }
    }

    public Task<bool> SaveChangesAsync(string? editorText = null, CancellationToken ct = default) => SaveAsync(editorText, ct);

    private async Task SaveFromEditorAsync(string? editorText, CancellationToken ct)
        => await SaveAsync(editorText, ct);

    private async Task<bool> SaveAsync(string? editorText, CancellationToken ct)
    {
        if (IsBinaryFile)
        {
            SetStatus("RemoteEditor.BinaryRejected", "ErrorTextStyle");
            return false;
        }

        if (editorText != null)
            Content = editorText;

        if (IsVeryLargeFileMode)
        {
            var confirmText = string.Format(L("RemoteEditor.VeryLargeSaveConfirm"), FileSizeText);
            var confirm = MessageBox.Show(
                confirmText,
                L("RemoteEditor.VeryLargeSaveTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
            {
                SetStatus("RemoteEditor.SaveCancelled", "WarningTextStyle");
                return false;
            }
        }

        IsBusy = true;
        SetStatus(IsLargeFileMode ? "RemoteEditor.SavingLarge" : "RemoteEditor.Saving", "InfoTextStyle");
        try
        {
            var bytes = TextEncodingDetector.Encode(Content, _encodingDetection);
            await _ssh.WriteFileBytesAsync(RemotePath, bytes, ct);
            SetStatus($"{L("RemoteEditor.Saved")}{DateTime.Now:HH:mm:ss}", "SuccessTextStyle", localize: false);
            _lastSavedContent = Content;
            IsDirty = false;
            SaveCompleted = true;
            return true;
        }
        catch (Exception ex)
        {
            SetStatus($"{L("RemoteEditor.SaveFailed")}{ex.Message}", "ErrorTextStyle", localize: false);
            return false;
        }
        finally { IsBusy = false; }
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
