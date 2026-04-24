namespace SlurmJobManager.Core.Models;

/// <summary>SSH connection profile for the Slurm controller node.</summary>
public class ConnectionProfile
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;

    /// <summary>Password (plain-text, stored only in memory).</summary>
    public string? Password { get; set; }

    /// <summary>Path to an SSH private key file (alternative to password).</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>Passphrase protecting the private key, if any.</summary>
    public string? PrivateKeyPassphrase { get; set; }

    /// <summary>Human-readable label for this profile.</summary>
    public string Label { get; set; } = string.Empty;
}
