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
    public string WindowTitle => $"{Application.Current?.TryFindResource("RemoteEditor.Title") as string ?? "编辑文件："} {FileName}";

    public string Content
    {
        get => _content;
        set => SetField(ref _content, value);
    }

    public bool IsBusy { get => _isBusy; private set => SetField(ref _isBusy, value); }
    public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }
    public string EncodingName { get => _encodingName; private set => SetField(ref _encodingName, value); }
    public bool IsBinaryFile { get => _isBinaryFile; private set => SetField(ref _isBinaryFile, value); }

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
        StatusMessage = Application.Current?.TryFindResource("RemoteEditor.Loading") as string ?? "加载文件…";
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
                                ?? (Application.Current?.TryFindResource("RemoteEditor.BinaryRejected") as string
                                    ?? "检测到二进制文件，已阻止打开。");
                return;
            }

            IsBinaryFile = false;
            Content = _encodingDetection.Encoding.GetString(bytes);
            if (!_encodingDetection.IsReliable)
            {
                StatusMessage = _encodingDetection.WarningMessage
                                ?? (Application.Current?.TryFindResource("RemoteEditor.EncodingUnknown") as string
                                    ?? "编码无法可靠识别，当前按 UTF-8 打开。");
            }
            else
            {
                StatusMessage = string.Empty;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"{Application.Current?.TryFindResource("RemoteEditor.LoadFailed") as string ?? "加载失败："}{ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        if (IsBinaryFile)
        {
            StatusMessage = Application.Current?.TryFindResource("RemoteEditor.BinaryRejected") as string
                            ?? "检测到二进制文件，已阻止保存。";
            return;
        }

        IsBusy = true;
        StatusMessage = Application.Current?.TryFindResource("RemoteEditor.Saving") as string ?? "保存中…";
        try
        {
            var bytes = TextEncodingDetector.Encode(Content, _encodingDetection);
            await _ssh.WriteFileBytesAsync(RemotePath, bytes, ct);
            StatusMessage = $"{Application.Current?.TryFindResource("RemoteEditor.Saved") as string ?? "已保存："}{DateTime.Now:HH:mm:ss}";
            SaveCompleted = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"{Application.Current?.TryFindResource("RemoteEditor.SaveFailed") as string ?? "保存失败："}{ex.Message}";
        }
        finally { IsBusy = false; }
    }
}
