using System.Windows.Input;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Models;

namespace SlurmJobManager.App.ViewModels;

public enum ConnectionStatus { Disconnected, Connecting, Connected, Reconnecting, Error }

/// <summary>Manages SSH connection settings, lifecycle, and encrypted profile persistence.</summary>
public sealed class ConnectionViewModel : ViewModelBase
{
    private readonly ISshClientService        _ssh;
    private readonly IConnectionProfileStore? _profileStore;

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
        set
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
        ConnectionStatus.Connected    => "● Connected",
        ConnectionStatus.Connecting   => "◎ Connecting…",
        ConnectionStatus.Reconnecting => "↺ Reconnecting…",
        ConnectionStatus.Error        => "✗ Error",
        _                             => "○ Disconnected",
    };

    public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }
    public bool IsBusy          { get => _isBusy;         private set => SetField(ref _isBusy, value); }
    public bool IsConnected     => Status == ConnectionStatus.Connected;

    /// <summary>Raised (on the UI thread) after a successful connection. Passes the connected username.</summary>
    public event Action<string>? ConnectionEstablished;

    public ICommand ConnectCommand        { get; }
    public ICommand DisconnectCommand     { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand BrowseKeyCommand      { get; }
    public ICommand SaveProfileCommand    { get; }
    public ICommand LoadProfileCommand    { get; }

    public ConnectionViewModel(ISshClientService ssh, IConnectionProfileStore? profileStore = null)
    {
        _ssh          = ssh ?? throw new ArgumentNullException(nameof(ssh));
        _profileStore = profileStore;

        ConnectCommand        = new AsyncRelayCommand(ConnectAsync,    () => !IsBusy && !IsConnected);
        DisconnectCommand     = new AsyncRelayCommand(DisconnectAsync, () => IsConnected);
        TestConnectionCommand = new AsyncRelayCommand(TestAsync,       () => !IsBusy);
        BrowseKeyCommand      = new RelayCommand(BrowseKey);
        SaveProfileCommand    = new AsyncRelayCommand(SaveProfileAsync,  () => _profileStore != null && !IsBusy);
        LoadProfileCommand    = new AsyncRelayCommand(LoadProfileAsync,  () => _profileStore != null && !IsBusy);
    }

    // ── Public API for reconnect logic ───────────────────────────────────────

    /// <summary>Re-establishes the connection using the current profile fields.</summary>
    public async Task<bool> TryReconnectAsync(CancellationToken ct)
    {
        try
        {
            await _ssh.ConnectAsync(BuildProfile(), ct);
            Status = ConnectionStatus.Connected;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Connection commands ──────────────────────────────────────────────────

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
            ConnectionEstablished?.Invoke(_username);
        }
        catch (Exception ex)
        {
            Status = ConnectionStatus.Error;
            StatusMessage = ClassifyError(ex);
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
            if (code == 0 && stdout.Contains("SLURM_TEST_OK"))
            {
                Status = ConnectionStatus.Connected;
                StatusMessage = "Test successful and connected.";
                ConnectionEstablished?.Invoke(_username);
            }
            else
            {
                Status = ConnectionStatus.Error;
                StatusMessage = $"Test failed: unexpected response (exit {code}).";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ClassifyError(ex);
            Status = ConnectionStatus.Error;
        }
        finally { IsBusy = false; }
    }

    // ── Profile persistence ──────────────────────────────────────────────────

    private async Task SaveProfileAsync(CancellationToken ct)
    {
        if (_profileStore is null) return;
        IsBusy = true;
        try
        {
            await _profileStore.SaveAsync(BuildProfile(), ct);
            StatusMessage = "Profile saved (credentials encrypted).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save profile failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private async Task LoadProfileAsync(CancellationToken ct)
    {
        if (_profileStore is null) return;
        IsBusy = true;
        try
        {
            var profile = await _profileStore.LoadAsync(ct);
            if (profile is null) { StatusMessage = "No saved profile found."; return; }

            Host                 = profile.Host;
            Port                 = profile.Port;
            Username             = profile.Username;
            PrivateKeyPath       = profile.PrivateKeyPath ?? string.Empty;
            Password             = profile.Password             ?? string.Empty;
            PrivateKeyPassphrase = profile.PrivateKeyPassphrase ?? string.Empty;
            StatusMessage = "Profile loaded.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Load profile failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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

    internal ConnectionProfile BuildProfile() => new()
    {
        Host                 = Host,
        Port                 = Port,
        Username             = Username,
        Password             = string.IsNullOrEmpty(PrivateKeyPath) ? Password : null,
        PrivateKeyPath       = string.IsNullOrEmpty(PrivateKeyPath) ? null : PrivateKeyPath,
        PrivateKeyPassphrase = string.IsNullOrEmpty(PrivateKeyPassphrase) ? null : PrivateKeyPassphrase,
    };

    /// <summary>Returns a user-friendly description based on the exception type.</summary>
    internal static string ClassifyError(Exception ex)
    {
        if (ex is Renci.SshNet.Common.SshAuthenticationException)
            return $"Authentication failed — check username/password or key. ({ex.Message})";
        if (ex is System.Net.Sockets.SocketException)
            return $"Network unreachable — check host/port and firewall. ({ex.Message})";
        if (ex is TimeoutException || ex is OperationCanceledException)
            return $"Connection timed out — server may be slow or unreachable.";
        return $"Error: {ex.Message}";
    }
}
