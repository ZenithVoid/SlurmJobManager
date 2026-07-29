namespace SlurmPilot.Core.Models;

/// <summary>Non-sensitive metadata for a recently used SSH connection.</summary>
public sealed class RecentConnectionRecord
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public string? Label { get; set; }
    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.UtcNow;
}
