using System.Windows;
using System.Windows.Input;
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
            Content = await _ssh.ReadTextFileAsync(RemotePath, ct);
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"{Application.Current?.TryFindResource("RemoteEditor.LoadFailed") as string ?? "加载失败："}{ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        IsBusy = true;
        StatusMessage = Application.Current?.TryFindResource("RemoteEditor.Saving") as string ?? "保存中…";
        try
        {
            await _ssh.WriteTextFileAsync(RemotePath, Content, ct);
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
