using SlurmPilot.Core.Interfaces;
using SlurmPilot.Core.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace SlurmPilot.Infrastructure.Logs;

/// <summary>
/// Structured application logger backed by Serilog.
/// Writes a daily-rolling log file under
/// <c>&lt;AppBaseDirectory&gt;\Data\logs\app-.log</c> and retains the last 14 files.
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
    /// Defaults to <c>&lt;AppBaseDirectory&gt;\Data\logs</c>.
    /// </param>
    /// <param name="minimumLevel">
    /// Minimum log event level to capture.
    /// Defaults to <see cref="LogEventLevel.Information"/>.
    /// </param>
    public SerilogAppLogger(string? logDirectory = null, LogEventLevel minimumLevel = LogEventLevel.Information)
    {
        var dir = logDirectory
            ?? LocalDataPaths.LogsDirectory;

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to create log directory '{dir}'. " +
                "Ensure the application has write permission to <AppBaseDirectory>\\Data\\logs " +
                "or supply an accessible path via the logDirectory parameter.", ex);
        }

        var logFilePath = Path.Combine(dir, "app-.log");

        _logger = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .WriteTo.File(
                logFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
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
