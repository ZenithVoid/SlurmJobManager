using SlurmJobManager.Core.Interfaces;
using Serilog;
using Serilog.Core;

namespace SlurmJobManager.Infrastructure.Logs;

/// <summary>
/// Structured application logger backed by Serilog.
/// Writes a daily-rolling log file under
/// <c>%AppData%\SlurmJobManager\logs\app-.log</c> and retains the last 14 files.
/// </summary>
public sealed class SerilogAppLogger : IAppLogger, IDisposable
{
    private readonly Logger _logger;
    private bool _disposed;

    /// <summary>
    /// Initialises the logger.
    /// </summary>
    /// <param name="logDirectory">
    /// Optional override for the directory that receives log files.
    /// Defaults to <c>%AppData%\SlurmJobManager\logs</c>.
    /// </param>
    public SerilogAppLogger(string? logDirectory = null)
    {
        var dir = logDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SlurmJobManager",
                "logs");

        Directory.CreateDirectory(dir);

        var logFilePath = Path.Combine(dir, "app-.log");

        _logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                logFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    /// <inheritdoc/>
    public void Debug(string message) => _logger.Debug(message);

    /// <inheritdoc/>
    public void Info(string message) => _logger.Information(message);

    /// <inheritdoc/>
    public void Warning(string message) => _logger.Warning(message);

    /// <inheritdoc/>
    public void Error(string message, Exception? ex = null)
    {
        if (ex is null)
            _logger.Error(message);
        else
            _logger.Error(ex, message);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _logger.Dispose();
    }
}
