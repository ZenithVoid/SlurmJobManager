using System.Windows.Input;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Models;

namespace SlurmJobManager.App.ViewModels;

public enum ConnectionStatus { Disconnected, Connecting, Connected, Error }

/// <summary>Manages SSH connection settings and lifecycle.</summary>
public sealed class ConnectionViewModel : ViewModelBase
{
    private readonly ISshClientService _ssh;

    private string _host = string.Empty;
    private int _port = 22;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private string _privateKeyPath = string.Empty;
    private string _privateKeyPassphrase = string.Empty;
    private ConnectionStatus _status = ConnectionStatus.Disconnected;
    private string _statusMessage = "Disconnected";
    private bool _isBusy;

    public string Host               { get => _host;                  set => SetField(ref _host, value); }
    public int Port                  { get => _port;                  set => SetField(ref _port, value); }
    public string Username           { get => _username;              set => SetField(ref _username, value); }
    public string Password           { get => _password;              set => SetField(ref _password, value); }
    public string PrivateKeyPath     { get => _privateKeyPath;        set => SetField(ref _privateKeyPath, value); }
    public string PrivateKeyPassphrase { get => _privateKeyPassphrase; set => SetField(ref _privateKeyPassphrase, value); }

    public ConnectionStatus Status
    {
        get => _status;
        private set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsConnected));
            }
        }
    }

    public string StatusText => Status switch
    {
        ConnectionStatus.Connected  => "● Connected",
        ConnectionStatus.Connecting => "◎ Connecting…",
        ConnectionStatus.Error      => "✗ Error",
        _                           => "○ Disconnected",
    };

    public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }
    public bool IsBusy          { get => _isBusy;         private set => SetField(ref _isBusy, value); }
    public bool IsConnected     => Status == ConnectionStatus.Connected;

    public ICommand ConnectCommand        { get; }
    public ICommand DisconnectCommand     { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand BrowseKeyCommand      { get; }

    public ConnectionViewModel(ISshClientService ssh)
    {
        _ssh = ssh ?? throw new ArgumentNullException(nameof(ssh));

        ConnectCommand        = new AsyncRelayCommand(ConnectAsync,    () => !IsBusy && !IsConnected);
        DisconnectCommand     = new AsyncRelayCommand(DisconnectAsync, () => IsConnected);
        TestConnectionCommand = new AsyncRelayCommand(TestAsync,       () => !IsBusy);
        BrowseKeyCommand      = new RelayCommand(BrowseKey);
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        IsBusy = true;
        Status = ConnectionStatus.Connecting;
        StatusMessage = "Connecting…";
        try
        {
            await _ssh.ConnectAsync(BuildProfile(), ct);
            Status = ConnectionStatus.Connected;
            StatusMessage = $"Connected to {_host}:{_port}";
        }
        catch (Exception ex)
        {
            Status = ConnectionStatus.Error;
            StatusMessage = $"Error: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private async Task DisconnectAsync(CancellationToken _)
    {
        await _ssh.DisconnectAsync();
        Status = ConnectionStatus.Disconnected;
        StatusMessage = "Disconnected";
    }

    private async Task TestAsync(CancellationToken ct)
    {
        IsBusy = true;
        StatusMessage = "Testing…";
        try
        {
            await _ssh.ConnectAsync(BuildProfile(), ct);
            var (stdout, _, code) = await _ssh.ExecuteAsync("echo SLURM_TEST_OK", ct);
            StatusMessage = code == 0 && stdout.Contains("SLURM_TEST_OK")
                ? "Test successful!"
                : $"Unexpected response (exit {code})";
            Status = ConnectionStatus.Connected;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Test failed: {ex.Message}";
            Status = ConnectionStatus.Error;
        }
        finally { IsBusy = false; }
    }

    private void BrowseKey()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Select SSH Private Key",
            Filter = "Key files (*.pem;*.ppk;*.key)|*.pem;*.ppk;*.key|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() == true)
            PrivateKeyPath = dlg.FileName;
    }

    private ConnectionProfile BuildProfile() => new()
    {
        Host                 = Host,
        Port                 = Port,
        Username             = Username,
        Password             = string.IsNullOrEmpty(PrivateKeyPath) ? Password : null,
        PrivateKeyPath       = string.IsNullOrEmpty(PrivateKeyPath) ? null : PrivateKeyPath,
        PrivateKeyPassphrase = string.IsNullOrEmpty(PrivateKeyPassphrase) ? null : PrivateKeyPassphrase,
    };
}
