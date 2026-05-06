namespace SlurmJobManager.Core.Interfaces;

/// <summary>
/// Represents a persistent interactive remote shell session.
/// </summary>
public interface IInteractiveShellSession : IDisposable
{
    /// <summary>Raised when raw terminal output is received from the remote shell.</summary>
    event EventHandler<string>? OutputReceived;

    /// <summary>Raised when the session is closed.</summary>
    event EventHandler? Closed;

    /// <summary>True when the shell session is still open for I/O.</summary>
    bool IsOpen { get; }

    /// <summary>Writes raw input data to the remote shell.</summary>
    Task WriteAsync(string data, CancellationToken ct = default);

    /// <summary>Resizes the remote terminal viewport.</summary>
    Task ResizeAsync(int cols, int rows, CancellationToken ct = default);

    /// <summary>Closes the interactive shell session.</summary>
    Task CloseAsync();
}
