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

    // ── Remote file-system helpers ───────────────────────────────────────────

    /// <summary>Returns the remote user's home directory (expands $HOME).</summary>
    Task<string> GetHomeDirectoryAsync(CancellationToken ct = default);

    /// <summary>Lists immediate subdirectory names under <paramref name="remotePath"/>.</summary>
    Task<IReadOnlyList<string>> ListDirectoriesAsync(string remotePath, CancellationToken ct = default);

    /// <summary>Lists immediate file names under <paramref name="remotePath"/>.</summary>
    Task<IReadOnlyList<string>> ListFilesAsync(string remotePath, CancellationToken ct = default);

    /// <summary>Reads a remote text file and returns its content.</summary>
    Task<string> ReadTextFileAsync(string remotePath, CancellationToken ct = default);

    /// <summary>Writes <paramref name="content"/> to a remote text file (creates or overwrites).</summary>
    Task WriteTextFileAsync(string remotePath, string content, CancellationToken ct = default);

    /// <summary>Returns true when the given remote path exists as a regular file.</summary>
    Task<bool> RemoteFileExistsAsync(string remotePath, CancellationToken ct = default);
}
