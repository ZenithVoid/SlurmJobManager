using SlurmPilot.Core.Interfaces;
using SlurmPilot.Core.Models;

namespace SlurmPilot.Infrastructure.Resilience;

/// <summary>
/// Provides exponential-backoff retry logic for transient SSH operations.
/// Non-retryable errors (e.g. authentication failures) are rethrown immediately.
/// </summary>
public static class RetryHelper
{
    /// <summary>
    /// Executes <paramref name="operation"/> with exponential back-off retry.
    /// </summary>
    /// <param name="operation">The async operation to attempt.</param>
    /// <param name="settings">Retry parameters.</param>
    /// <param name="logger">Optional logger for retry events.</param>
    /// <param name="operationName">Human-readable label for log messages.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        AppSettings                   settings,
        IAppLogger?                   logger,
        string                        operationName,
        CancellationToken             ct = default)
    {
        int attempts = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await operation(ct);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsRetryable(ex))
            {
                if (attempts >= settings.MaxRetryAttempts)
                    throw;   // last attempt exhausted — propagate

                attempts++;
                var delay = TimeSpan.FromSeconds(
                    settings.RetryBaseDelay.TotalSeconds * Math.Pow(2, attempts - 1));
                logger?.Warning(
                    $"[Retry {attempts}/{settings.MaxRetryAttempts}] {operationName} failed: {ex.Message}. " +
                    $"Retrying in {delay.TotalSeconds:F0}s…");
                await Task.Delay(delay, ct);
            }
        }
    }

    /// <summary>
    /// Executes <paramref name="operation"/> with exponential back-off retry and returns its result.
    /// </summary>
    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        AppSettings                       settings,
        IAppLogger?                       logger,
        string                            operationName,
        CancellationToken                 ct = default)
    {
        int attempts = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await operation(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsRetryable(ex))
            {
                if (attempts >= settings.MaxRetryAttempts)
                    throw;   // last attempt exhausted — propagate

                attempts++;
                var delay = TimeSpan.FromSeconds(
                    settings.RetryBaseDelay.TotalSeconds * Math.Pow(2, attempts - 1));
                logger?.Warning(
                    $"[Retry {attempts}/{settings.MaxRetryAttempts}] {operationName} failed: {ex.Message}. " +
                    $"Retrying in {delay.TotalSeconds:F0}s…");
                await Task.Delay(delay, ct);
            }
        }
    }

    /// <summary>
    /// Determines whether an exception represents a transient failure that may succeed on retry.
    /// Authentication failures are never retried.
    /// </summary>
    public static bool IsRetryable(Exception ex)
    {
        // Never retry authentication failures
        if (ex is Renci.SshNet.Common.SshAuthenticationException) return false;

        // Retry general SSH exceptions (network glitches, timeouts, etc.)
        if (ex is Renci.SshNet.Common.SshException)              return true;
        if (ex is System.Net.Sockets.SocketException)            return true;
        if (ex is TimeoutException)                              return true;
        if (ex is IOException)                                   return true;

        return false;
    }
}
