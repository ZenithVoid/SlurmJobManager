using SlurmJobManager.Core.Models;

namespace SlurmJobManager.Core.Interfaces;

/// <summary>Low-level SSH connectivity and command execution.</summary>
public interface ISshClientService : IDisposable
{
    /// <summary>Opens the SSH connection using the supplied profile.</summary>
    Task ConnectAsync(ConnectionProfile profile, CancellationToken ct = default);

    /// <summary>Returns true when the underlying connection is open.</summary>
    bool IsConnected { get; }

    /// <summary>Executes a remote command and returns (stdout, stderr, exitCode).</summary>
    Task<(string StdOut, string StdErr, int ExitCode)> ExecuteAsync(string command, CancellationToken ct = default);

    /// <summary>Uploads a local file to a remote path via SFTP.</summary>
    Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct = default);

    /// <summary>Downloads a remote file to a local path via SFTP.</summary>
    Task DownloadFileAsync(string remotePath, string localPath, CancellationToken ct = default);

    /// <summary>Closes the connection.</summary>
    Task DisconnectAsync();
}
