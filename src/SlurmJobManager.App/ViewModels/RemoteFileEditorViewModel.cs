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
    private readonly ISshClientService _ssh;

    private string _content = string.Empty;
    private bool _isBusy;
    private string _statusMessage = string.Empty;
    private string _encodingName = "UTF-8";
    private bool _isBinaryFile;
    private bool _isDirty;
    private string _statusStyleKey = "InfoTextStyle";
    private string _lastSavedContent = string.Empty;
    private bool _suppressDirtyTracking;
    private TextEncodingDetectionResult _encodingDetection = new()
    {
        Encoding = new System.Text.UTF8Encoding(false),
        DisplayName = "UTF-8",
        HasBom = false,
        IsReliable = true,
        IsBinaryLike = false,
    };

    public string RemotePath { get; }
    public string FileName   => RemotePath.Contains('/') ? RemotePath[(RemotePath.LastIndexOf('/') + 1)..] : RemotePath;

    /// <summary>Formatted window title including the filename, resolved from localization resources at runtime.</summary>
    public string WindowTitle => $"{L("RemoteEditor.Title")} {FileName}";

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

    /// <summary>Set to <c>true</c> after a successful save so the view can close.</summary>
    public bool SaveCompleted { get; private set; }

    public ICommand SaveCommand { get; }

    public RemoteFileEditorViewModel(ISshClientService ssh, string remotePath)
    {
        _ssh        = ssh ?? throw new ArgumentNullException(nameof(ssh));
        RemotePath  = remotePath;
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy);
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        SetStatus("RemoteEditor.Loading", "InfoTextStyle");
        try
        {
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
            else
            {
                SetStatus(string.Empty, "InfoTextStyle");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"{L("RemoteEditor.LoadFailed")}{ex.Message}", "ErrorTextStyle", localize: false);
        }
        finally { IsBusy = false; }
    }

    public Task<bool> SaveChangesAsync(CancellationToken ct = default) => SaveAsync(ct);

    private async Task<bool> SaveAsync(CancellationToken ct)
    {
        if (IsBinaryFile)
        {
            SetStatus("RemoteEditor.BinaryRejected", "ErrorTextStyle");
            return false;
        }

        IsBusy = true;
        SetStatus("RemoteEditor.Saving", "InfoTextStyle");
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
}
