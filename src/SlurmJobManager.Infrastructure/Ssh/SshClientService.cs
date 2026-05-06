using System.Text;
using Renci.SshNet;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Models;

namespace SlurmJobManager.Infrastructure.Ssh;

/// <summary>
/// SSH connectivity backed by SSH.NET (Renci.SshNet).
/// Supports both password and private-key authentication, configurable
/// timeouts, and proper propagation of <see cref="CancellationToken"/>.
/// </summary>
public sealed class SshClientService : ISshClientService
{
    private readonly AppSettings _settings;
    private readonly List<IInteractiveShellSession> _interactiveSessions = new();
    private readonly object _sessionLock = new();
    private readonly object _clientLock = new();

    private SshClient? _sshClient;
    private SftpClient? _sftpClient;
    private bool _disposed;

    public SshClientService(AppSettings? settings = null)
    {
        _settings = settings ?? new AppSettings();
    }

    public bool IsConnected
    {
        get
        {
            lock (_clientLock)
            {
                if (_disposed) return false;
                if (_sshClient == null) return false;
                try
                {
                    return _sshClient.IsConnected;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }
        }
    }

    public Task ConnectAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        Disconnect();

        ct.ThrowIfCancellationRequested();

        AuthenticationMethod auth = BuildAuthMethod(profile);
        var connInfo = new ConnectionInfo(profile.Host, profile.Port, profile.Username, auth)
        {
            Timeout = _settings.ConnectionTimeout,
        };

        var sshClient = new SshClient(connInfo);
        var sftpClient = new SftpClient(connInfo);
        lock (_clientLock)
        {
            _sshClient = sshClient;
            _sftpClient = sftpClient;
        }

        // SSH.NET Connect() is synchronous; run off the thread-pool so the UI stays responsive.
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            _sshClient!.Connect();
            ct.ThrowIfCancellationRequested();
            _sftpClient!.Connect();
        }, ct);
    }

    public async Task<(string StdOut, string StdErr, int ExitCode)> ExecuteAsync(
        string command, CancellationToken ct = default)
    {
        EnsureConnected();

        using var cmd = _sshClient!.CreateCommand(command);
        cmd.CommandTimeout = _settings.CommandTimeout;

        // Link the caller's token with the command timeout so whichever fires first wins.
        using var timeoutCts = new CancellationTokenSource(_settings.CommandTimeout);
        using var linked     = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        await Task.Run(() => cmd.Execute(), linked.Token);
        return (cmd.Result, cmd.Error, cmd.ExitStatus ?? -1);
    }

    public Task<IInteractiveShellSession> StartInteractiveShellSessionAsync(
        string terminalName = "xterm-256color",
        int cols = 120,
        int rows = 36,
        CancellationToken ct = default)
    {
        EnsureConnected();
        cols = Math.Max(2, cols);
        rows = Math.Max(2, rows);

        return Task.Run<IInteractiveShellSession>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var stream = _sshClient!.CreateShellStream(
                terminalName,
                (uint)cols,
                (uint)rows,
                0,
                0,
                4096);

            var session = new SshInteractiveShellSession(stream);
            session.Closed += OnInteractiveSessionClosed;
            lock (_sessionLock)
                _interactiveSessions.Add(session);
            return session;
        }, ct);
    }

    public Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct = default)
    {
        EnsureConnected();
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(localPath);
            _sftpClient!.UploadFile(stream, remotePath, canOverride: true);
        }, ct);
    }

    public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken ct = default)
    {
        EnsureConnected();
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var stream = File.OpenWrite(localPath);
            _sftpClient!.DownloadFile(remotePath, stream);
        }, ct);
    }

    public Task DisconnectAsync()
    {
        Disconnect();
        return Task.CompletedTask;
    }

    // ── Remote file-system helpers ───────────────────────────────────────────

    public async Task<string> GetHomeDirectoryAsync(CancellationToken ct = default)
    {
        var (stdout, _, _) = await ExecuteAsync("echo $HOME", ct);
        return stdout.Trim();
    }

    public Task<IReadOnlyList<string>> ListDirectoriesAsync(string remotePath, CancellationToken ct = default)
    {
        EnsureConnected();
        return Task.Run<IReadOnlyList<string>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var entries = _sftpClient!.ListDirectory(remotePath);
            return entries
                .Where(e => e.IsDirectory && e.Name != "." && e.Name != "..")
                .Select(e => e.Name)
                .OrderBy(n => n)
                .ToList();
        }, ct);
    }

    public Task<IReadOnlyList<string>> ListFilesAsync(string remotePath, CancellationToken ct = default)
    {
        EnsureConnected();
        return Task.Run<IReadOnlyList<string>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var entries = _sftpClient!.ListDirectory(remotePath);
                return entries
                    .Where(e => e.IsRegularFile)
                    .Select(e => e.Name)
                    .OrderBy(n => n)
                    .ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SshClientService.ListFilesAsync] {remotePath}: {ex.Message}");
                return Array.Empty<string>();
            }
        }, ct);
    }

    public Task<string> ReadTextFileAsync(string remotePath, CancellationToken ct = default)
    {
        EnsureConnected();
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var ms = new MemoryStream();
            _sftpClient!.DownloadFile(remotePath, ms);
            return Encoding.UTF8.GetString(ms.ToArray());
        }, ct);
    }

    public Task<byte[]> ReadFileBytesAsync(string remotePath, CancellationToken ct = default)
    {
        EnsureConnected();
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var ms = new MemoryStream();
            _sftpClient!.DownloadFile(remotePath, ms);
            return ms.ToArray();
        }, ct);
    }

    public Task WriteTextFileAsync(string remotePath, string content, CancellationToken ct = default)
    {
        EnsureConnected();
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var bytes = Encoding.UTF8.GetBytes(content);
            using var ms = new MemoryStream(bytes);
            _sftpClient!.UploadFile(ms, remotePath, canOverride: true);
        }, ct);
    }

    public Task WriteFileBytesAsync(string remotePath, byte[] content, CancellationToken ct = default)
    {
        EnsureConnected();
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var ms = new MemoryStream(content, writable: false);
            _sftpClient!.UploadFile(ms, remotePath, canOverride: true);
        }, ct);
    }

    public async Task<bool> RemoteFileExistsAsync(string remotePath, CancellationToken ct = default)
    {
        var (stdout, _, _) = await ExecuteAsync(
            $"test -f {EscapeShellArg(remotePath)} && echo 1 || echo 0", ct);
        return stdout.Trim() == "1";
    }

    public async Task<bool> RemoteDirectoryExistsAsync(string remotePath, CancellationToken ct = default)
    {
        var (stdout, _, _) = await ExecuteAsync(
            $"test -d {EscapeShellArg(remotePath)} && echo 1 || echo 0", ct);
        return stdout.Trim() == "1";
    }

    public Task<long> GetRemoteFileSizeAsync(string remotePath, CancellationToken ct = default)
    {
        EnsureConnected();
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var attrs = _sftpClient!.GetAttributes(remotePath);
            return attrs.Size;
        }, ct);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private void Disconnect()
    {
        SftpClient? sftpClient;
        SshClient? sshClient;
        lock (_clientLock)
        {
            sftpClient = _sftpClient;
            sshClient = _sshClient;
            _sftpClient = null;
            _sshClient = null;
        }

        List<IInteractiveShellSession> sessionsToClose;
        lock (_sessionLock)
        {
            sessionsToClose = _interactiveSessions.ToList();
            _interactiveSessions.Clear();
        }

        foreach (var session in sessionsToClose)
        {
            try { session.Closed -= OnInteractiveSessionClosed; } catch { /* best effort */ }
            try { session.Dispose(); } catch (Exception) { /* best-effort cleanup */ }
        }

        try { sftpClient?.Disconnect(); } catch (Exception) { /* best-effort cleanup */ }
        try { sshClient?.Disconnect(); } catch (Exception) { /* best-effort cleanup */ }
        try { sftpClient?.Dispose(); } catch (Exception) { /* best-effort cleanup */ }
        try { sshClient?.Dispose(); } catch (Exception) { /* best-effort cleanup */ }
    }

    private void OnInteractiveSessionClosed(object? sender, EventArgs e)
    {
        if (sender is not IInteractiveShellSession session) return;
        lock (_sessionLock)
            _interactiveSessions.Remove(session);
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
            throw new InvalidOperationException("SSH client is not connected. Call ConnectAsync first.");
    }

    private static AuthenticationMethod BuildAuthMethod(ConnectionProfile profile)
    {
        if (!string.IsNullOrEmpty(profile.PrivateKeyPath))
        {
            PrivateKeyFile keyFile = string.IsNullOrEmpty(profile.PrivateKeyPassphrase)
                ? new PrivateKeyFile(profile.PrivateKeyPath)
                : new PrivateKeyFile(profile.PrivateKeyPath, profile.PrivateKeyPassphrase);
            return new PrivateKeyAuthenticationMethod(profile.Username, keyFile);
        }

        return new PasswordAuthenticationMethod(profile.Username, profile.Password ?? string.Empty);
    }

    /// <summary>Single-quotes a path for use in a POSIX shell command.</summary>
    private static string EscapeShellArg(string arg)
        => "'" + arg.Replace("'", "'\\''") + "'";

    public void Dispose()
    {
        lock (_clientLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Disconnect();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SshClientService));
    }
}
