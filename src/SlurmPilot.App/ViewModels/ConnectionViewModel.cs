using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Input;
using Renci.SshNet.Common;
using SlurmPilot.App.ViewModels.Dialogs;
using SlurmPilot.App.Views.Dialogs;
using SlurmPilot.Core.Interfaces;
using SlurmPilot.Core.Models;

namespace SlurmPilot.App.ViewModels;

public enum ConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Error,
    Testing,
    TestSucceeded,
    TestFailed,
    ConnectFailed,
}

/// <summary>Manages SSH connection settings, lifecycle, profile persistence, and recent connections.</summary>
public sealed class ConnectionViewModel : ViewModelBase
{
    private readonly ISshClientService _ssh;
    private readonly IConnectionProfileStore? _profileStore;
    private readonly IRecentConnectionService? _recentConnectionService;
    private readonly IAppLogger? _logger;

    private string _host = string.Empty;
    private int _port = 22;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private string _privateKeyPath = string.Empty;
    private string _privateKeyPassphrase = string.Empty;
    private ConnectionStatus _status = ConnectionStatus.Disconnected;
    private string _statusMessage = string.Empty;
    private bool _isBusy;
    private RecentConnectionRecord? _selectedRecentConnection;

    public string Host { get => _host; set => SetField(ref _host, value); }
    public int Port { get => _port; set => SetField(ref _port, value); }
    public string Username { get => _username; set => SetField(ref _username, value); }
    public string Password { get => _password; set => SetField(ref _password, value); }
    public string PrivateKeyPath { get => _privateKeyPath; set => SetField(ref _privateKeyPath, value); }
    public string PrivateKeyPassphrase { get => _privateKeyPassphrase; set => SetField(ref _privateKeyPassphrase, value); }

    public ConnectionStatus Status
    {
        get => _status;
        set
        {
            if (!SetField(ref _status, value)) return;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsConnected));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string StatusText => Status switch
    {
        ConnectionStatus.Connected => $"● {L("Status.Connected")}",
        ConnectionStatus.Connecting => $"◎ {L("Status.Connecting")}",
        ConnectionStatus.Testing => $"◌ {L("Status.Testing")}",
        ConnectionStatus.TestSucceeded => $"✓ {L("Status.TestSuccess")}",
        ConnectionStatus.TestFailed => $"✗ {L("Status.TestFailed")}",
        ConnectionStatus.ConnectFailed => $"✗ {L("Status.ConnectionFailed")}",
        ConnectionStatus.Reconnecting => $"↺ {L("Conn.StatusReconnecting")}",
        ConnectionStatus.Error => $"✗ {L("Status.Error")}",
        _ => $"○ {L("Status.Idle")}",
    };

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsConnected => _ssh.IsConnected;

    public ObservableCollection<RecentConnectionRecord> RecentConnections { get; } = new();

    public RecentConnectionRecord? SelectedRecentConnection
    {
        get => _selectedRecentConnection;
        set => SetField(ref _selectedRecentConnection, value);
    }

    /// <summary>Raised (on the UI thread) after a successful connection. Passes the connected username.</summary>
    public event Action<string>? ConnectionEstablished;

    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand BrowseKeyCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand LoadProfileCommand { get; }
    public ICommand ApplyRecentConnectionCommand { get; }
    public ICommand DeleteRecentConnectionCommand { get; }

    public ConnectionViewModel(
        ISshClientService ssh,
        IConnectionProfileStore? profileStore = null,
        IRecentConnectionService? recentConnectionService = null,
        IAppLogger? logger = null)
    {
        _ssh = ssh ?? throw new ArgumentNullException(nameof(ssh));
        _profileStore = profileStore;
        _recentConnectionService = recentConnectionService;
        _logger = logger;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsBusy && !IsConnected);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => !IsBusy && IsConnected);
        TestConnectionCommand = new AsyncRelayCommand(TestAsync, () => !IsBusy);
        BrowseKeyCommand = new RelayCommand(BrowseKey);
        SaveProfileCommand = new AsyncRelayCommand(SaveProfileAsync, () => _profileStore != null && !IsBusy);
        LoadProfileCommand = new AsyncRelayCommand(LoadProfileAsync, () => _profileStore != null && !IsBusy);
        ApplyRecentConnectionCommand = new RelayCommand<RecentConnectionRecord>(ApplyRecentConnection);
        DeleteRecentConnectionCommand = new AsyncRelayCommand<RecentConnectionRecord>(DeleteRecentConnectionAsync, r => !IsBusy && r != null);
        _statusMessage = L("Status.Idle");

        if (_recentConnectionService != null)
            _ = SafeRefreshRecentConnectionsAsync();
    }

    /// <summary>Re-establishes the connection using the current profile fields.</summary>
    public async Task<bool> TryReconnectAsync(CancellationToken ct)
    {
        _logger?.Info($"SSH reconnect requested. Host={Host.Trim()}, Port={(Port <= 0 ? 22 : Port)}, User={Username.Trim()}, AuthMode={(string.IsNullOrWhiteSpace(PrivateKeyPath) ? "password" : "private-key")}");
        try
        {
            await _ssh.ConnectAsync(BuildProfile(), ct);
            Status = ConnectionStatus.Connected;
            StatusMessage = BuildConnectedStatusMessage();
            await AddRecentConnectionAsync(ct);
            _logger?.Info($"SSH reconnect succeeded. Host={Host.Trim()}, User={Username.Trim()}");
            return true;
        }
        catch
        {
            _logger?.Warning($"SSH reconnect failed. Host={Host.Trim()}, User={Username.Trim()}");
            return false;
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        IsBusy = true;
        Status = ConnectionStatus.Connecting;
        StatusMessage = L("Status.Connecting");
        _logger?.Info($"SSH connect requested. Host={Host.Trim()}, Port={(Port <= 0 ? 22 : Port)}, User={Username.Trim()}, AuthMode={(string.IsNullOrWhiteSpace(PrivateKeyPath) ? "password" : "private-key")}");
        try
        {
            await _ssh.ConnectAsync(BuildProfile(), ct);
            Status = ConnectionStatus.Connected;
            StatusMessage = BuildConnectedStatusMessage();
            await AddRecentConnectionAsync(ct);
            ConnectionEstablished?.Invoke(_username);
            _logger?.Info($"SSH connect succeeded. Host={Host.Trim()}, User={Username.Trim()}");
        }
        catch (Exception ex)
        {
            Status = ConnectionStatus.ConnectFailed;
            StatusMessage = ClassifyError(ex, usingKeyAuth: !string.IsNullOrWhiteSpace(PrivateKeyPath));
            _logger?.Error($"SSH connect failed. Host={Host.Trim()}, Port={(Port <= 0 ? 22 : Port)}, User={Username.Trim()}", ex);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsConnected));
        }
    }

    private async Task DisconnectAsync(CancellationToken _)
    {
        await _ssh.DisconnectAsync();
        Status = ConnectionStatus.Disconnected;
        StatusMessage = L("Status.Idle");
        _logger?.Info("SSH disconnected by user.");
        OnPropertyChanged(nameof(IsConnected));
    }

    private async Task TestAsync(CancellationToken ct)
    {
        IsBusy = true;
        Status = ConnectionStatus.Testing;
        StatusMessage = L("Conn.Testing");
        _logger?.Info($"SSH test requested. Host={Host.Trim()}, Port={(Port <= 0 ? 22 : Port)}, User={Username.Trim()}, AuthMode={(string.IsNullOrWhiteSpace(PrivateKeyPath) ? "password" : "private-key")}");
        try
        {
            await _ssh.TestConnectionAsync(BuildProfile(), ct);
            Status = ConnectionStatus.TestSucceeded;
            StatusMessage = BuildTestSuccessStatusMessage();
            _logger?.Info($"SSH test succeeded. Host={Host.Trim()}, User={Username.Trim()}");
        }
        catch (Exception ex)
        {
            Status = ConnectionStatus.TestFailed;
            StatusMessage = ClassifyError(ex, usingKeyAuth: !string.IsNullOrWhiteSpace(PrivateKeyPath));
            _logger?.Error($"SSH test failed. Host={Host.Trim()}, Port={(Port <= 0 ? 22 : Port)}, User={Username.Trim()}", ex);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsConnected));
        }
    }

    private async Task SaveProfileAsync(CancellationToken ct)
    {
        if (_profileStore is null) return;
        IsBusy = true;
        try
        {
            await _profileStore.SaveAsync(BuildProfile(), ct);
            StatusMessage = L("Conn.ProfileSaved");
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(L("Conn.SaveProfileFailed"), ex.Message);
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
            if (profile is null)
            {
                StatusMessage = L("Conn.NoSavedProfile");
                return;
            }

            Host = profile.Host;
            Port = profile.Port;
            Username = profile.Username;
            PrivateKeyPath = profile.PrivateKeyPath ?? string.Empty;
            Password = profile.Password ?? string.Empty;
            PrivateKeyPassphrase = profile.PrivateKeyPassphrase ?? string.Empty;
            StatusMessage = L("Conn.ProfileLoaded");
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(L("Conn.LoadProfileFailed"), ex.Message);
        }
        finally { IsBusy = false; }
    }

    private void BrowseKey()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select SSH Private Key",
            Filter = "Key files (*.pem;*.ppk;*.key)|*.pem;*.ppk;*.key|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() == true)
            PrivateKeyPath = dlg.FileName;
    }

    internal ConnectionProfile BuildProfile() => new()
    {
        Host = Host.Trim(),
        Port = Port <= 0 ? 22 : Port,
        Username = Username.Trim(),
        Password = string.IsNullOrEmpty(PrivateKeyPath) ? Password : null,
        PrivateKeyPath = string.IsNullOrEmpty(PrivateKeyPath) ? null : PrivateKeyPath,
        PrivateKeyPassphrase = string.IsNullOrEmpty(PrivateKeyPassphrase) ? null : PrivateKeyPassphrase,
    };

    internal static string ClassifyError(Exception ex)
        => ClassifyError(ex, usingKeyAuth: false);

    internal static string ClassifyError(Exception ex, bool usingKeyAuth)
    {
        var root = Unwrap(ex);
        var raw = root.Message;

        if (root is SshOperationTimeoutException || root is TimeoutException || root is OperationCanceledException)
            return string.Format(L("Conn.ErrTimeout"), raw);

        if (root is SocketException socketEx)
            return socketEx.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => string.Format(L("Conn.ErrPortRefused"), raw),
                SocketError.HostNotFound or SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.NoData
                    => string.Format(L("Conn.ErrHostUnreachable"), raw),
                SocketError.TimedOut => string.Format(L("Conn.ErrTimeout"), raw),
                _ => string.Format(L("Conn.ErrNetwork"), raw),
            };

        if (root is SshAuthenticationException)
            return usingKeyAuth
                ? string.Format(L("Conn.ErrKeyAuth"), raw)
                : string.Format(L("Conn.ErrUserPassword"), raw);

        if (root is SshConnectionException)
        {
            if (raw.Contains("host key", StringComparison.OrdinalIgnoreCase) ||
                raw.Contains("fingerprint", StringComparison.OrdinalIgnoreCase))
                return string.Format(L("Conn.ErrHostKey"), raw);

            if (raw.Contains("timeout", StringComparison.OrdinalIgnoreCase))
                return string.Format(L("Conn.ErrTimeout"), raw);
        }

        if (root is SshPassPhraseNullOrEmptyException)
            return string.Format(L("Conn.ErrKeyAuth"), raw);

        return string.Format(L("Conn.ErrUnknown"), raw);
    }

    private static Exception Unwrap(Exception ex)
    {
        if (ex is AggregateException aggregate)
        {
            var flattened = aggregate.Flatten().InnerExceptions;
            return flattened.FirstOrDefault(e => e is not OperationCanceledException)
                   ?? flattened.FirstOrDefault()
                   ?? ex;
        }
        return ex.InnerException ?? ex;
    }

    private string BuildConnectedStatusMessage()
    {
        var baseText = string.Format(L("Conn.ConnectedTo"), Host, Port);
        var fingerprint = BuildFingerprintHint();
        return string.IsNullOrWhiteSpace(fingerprint) ? baseText : $"{baseText}\n{fingerprint}";
    }

    private string BuildTestSuccessStatusMessage()
    {
        var baseText = L("Conn.TestSuccess");
        var fingerprint = BuildFingerprintHint();
        return string.IsNullOrWhiteSpace(fingerprint) ? baseText : $"{baseText}\n{fingerprint}";
    }

    private string BuildFingerprintHint()
    {
        if (string.IsNullOrWhiteSpace(_ssh.LastServerFingerprint))
            return string.Empty;

        return string.Format(L("Conn.ServerFingerprint"), _ssh.LastServerFingerprint);
    }

    private async Task AddRecentConnectionAsync(CancellationToken ct)
    {
        if (_recentConnectionService is null) return;

        var record = new RecentConnectionRecord
        {
            Host = Host.Trim(),
            Port = Port <= 0 ? 22 : Port,
            Username = Username.Trim(),
            LastUsedAt = DateTimeOffset.UtcNow,
        };

        await _recentConnectionService.AddOrUpdateAsync(record, ct);
        await RefreshRecentConnectionsAsync(ct);
    }

    private async Task RefreshRecentConnectionsAsync(CancellationToken ct)
    {
        if (_recentConnectionService is null) return;

        var records = await _recentConnectionService.GetRecentAsync(ct);
        RecentConnections.Clear();
        foreach (var record in records)
            RecentConnections.Add(record);
        OnPropertyChanged(nameof(RecentConnections));
    }

    private async Task SafeRefreshRecentConnectionsAsync()
    {
        try
        {
            await RefreshRecentConnectionsAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(L("Conn.LoadRecentFailed"), ex.Message);
        }
    }

    private void ApplyRecentConnection(RecentConnectionRecord? record)
    {
        if (record is null) return;
        Host = record.Host;
        Port = record.Port;
        Username = record.Username;
        StatusMessage = string.Format(L("Conn.RecentApplied"), record.Username, record.Host, record.Port);
    }

    private async Task DeleteRecentConnectionAsync(RecentConnectionRecord? record, CancellationToken ct)
    {
        if (_recentConnectionService is null || record is null) return;
        if (!ConfirmDeleteRecentConnection(record)) return;

        await _recentConnectionService.RemoveAsync(record.Host, record.Port, record.Username, ct);
        await RefreshRecentConnectionsAsync(ct);
        StatusMessage = string.Format(L("Conn.RecentDeleted"), record.Username, record.Host, record.Port);
    }

    private bool ConfirmDeleteRecentConnection(RecentConnectionRecord record)
    {
        var vm = new ConfirmationDialogViewModel(
            title: L("Conn.RecentDeleteTitle"),
            message: string.Format(L("Conn.RecentDeleteConfirm"), record.Username, record.Host, record.Port),
            details: record.Label ?? string.Empty,
            confirmButtonText: L("Dialog.Confirm"),
            cancelButtonText: null,
            isWarning: true);
        var dialog = new ConfirmationDialogView { DataContext = vm };
        if (Application.Current?.MainWindow is Window owner)
            dialog.Owner = owner;
        return dialog.ShowDialog() == true;
    }

    private static string L(string key)
        => Application.Current?.TryFindResource(key) as string ?? key;

    internal void NotifyLocaleChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(RecentConnections));
    }
}
