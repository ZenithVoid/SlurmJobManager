using Renci.SshNet;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Models;

namespace SlurmJobManager.Infrastructure.Ssh;

/// <summary>
/// SSH connectivity backed by SSH.NET (Renci.SshNet).
/// Supports both password and private-key authentication.
/// </summary>
public sealed class SshClientService : ISshClientService
{
    private SshClient? _sshClient;
    private SftpClient? _sftpClient;

    public bool IsConnected => _sshClient?.IsConnected ?? false;

    public Task ConnectAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        Disconnect();

        AuthenticationMethod auth = BuildAuthMethod(profile);
        var connInfo = new ConnectionInfo(profile.Host, profile.Port, profile.Username, auth);

        _sshClient = new SshClient(connInfo);
        _sftpClient = new SftpClient(connInfo);

        _sshClient.Connect();
        _sftpClient.Connect();

        return Task.CompletedTask;
    }

    public async Task<(string StdOut, string StdErr, int ExitCode)> ExecuteAsync(
        string command, CancellationToken ct = default)
    {
        EnsureConnected();

        using var cmd = _sshClient!.CreateCommand(command);
        await Task.Run(() => cmd.Execute(), ct);
        return (cmd.Result, cmd.Error, cmd.ExitStatus ?? -1);
    }

    public Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct = default)
    {
        EnsureConnected();
        using var stream = File.OpenRead(localPath);
        _sftpClient!.UploadFile(stream, remotePath, canOverride: true);
        return Task.CompletedTask;
    }

    public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken ct = default)
    {
        EnsureConnected();
        using var stream = File.OpenWrite(localPath);
        _sftpClient!.DownloadFile(remotePath, stream);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        Disconnect();
        return Task.CompletedTask;
    }

    private void Disconnect()
    {
        try { _sftpClient?.Disconnect(); } catch (Exception) { /* best-effort cleanup */ }
        try { _sshClient?.Disconnect(); } catch (Exception) { /* best-effort cleanup */ }
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

    public void Dispose()
    {
        Disconnect();
        _sftpClient?.Dispose();
        _sshClient?.Dispose();
    }
}
