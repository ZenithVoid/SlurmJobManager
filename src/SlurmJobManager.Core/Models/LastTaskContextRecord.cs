namespace SlurmJobManager.Core.Models;

/// <summary>
/// Last used task context scoped to a single SSH connection identity (host + username).
/// </summary>
public sealed class LastTaskContextRecord
{
    public string Host { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string ScopeKey { get; set; } = string.Empty;
    public string? RootDirectory { get; set; }
    public string? TaskId { get; set; }
    public string? RemoteWorkDir { get; set; }
    public string? CurrentTaskFilesPath { get; set; }
    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.UtcNow;

    public static string BuildScopeKey(string host, string username)
        => $"{(host ?? string.Empty).Trim().ToLowerInvariant()}__{(username ?? string.Empty).Trim().ToLowerInvariant()}";
}
