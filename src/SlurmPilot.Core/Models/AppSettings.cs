namespace SlurmPilot.Core.Models;

/// <summary>Centralised application-wide timeout, retry, and polling settings.</summary>
public sealed class AppSettings
{
    /// <summary>Timeout for establishing the SSH connection.</summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Timeout for each individual SSH command execution.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Timeout for a single log-chunk fetch operation.</summary>
    public TimeSpan LogFetchTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Maximum number of retry attempts for transient failures.</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Base delay for the first retry (doubles each subsequent attempt).</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum consecutive reconnect attempts before entering the Error state.</summary>
    public int MaxReconnectAttempts { get; set; } = 5;
}
