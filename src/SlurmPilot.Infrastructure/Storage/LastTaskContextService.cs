using System.Text.Json;
using System.Text.Json.Serialization;
using SlurmPilot.Core.Interfaces;
using SlurmPilot.Core.Models;
using SlurmPilot.Core.Services;

namespace SlurmPilot.Infrastructure.Storage;

/// <summary>
/// JSON-backed local storage for per-connection task context restore.
/// </summary>
public sealed class LastTaskContextService : ILastTaskContextService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath = LocalDataPaths.LastTaskContextsFilePath;
    private readonly IAppLogger? _logger;

    public LastTaskContextService(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    public async Task<LastTaskContextRecord?> GetByConnectionAsync(string host, string username, CancellationToken ct = default)
    {
        var normalizedHost = host?.Trim() ?? string.Empty;
        var normalizedUsername = username?.Trim() ?? string.Empty;
        if (normalizedHost.Length == 0 || normalizedUsername.Length == 0)
            return null;

        var records = await ReadAllAsync(ct);
        return records.FirstOrDefault(x =>
            string.Equals(x.Host, normalizedHost, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Username, normalizedUsername, StringComparison.OrdinalIgnoreCase));
    }

    public async Task UpsertAsync(LastTaskContextRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var host = record.Host?.Trim() ?? string.Empty;
        var username = record.Username?.Trim() ?? string.Empty;
        if (host.Length == 0 || username.Length == 0)
            return;

        var records = await ReadAllAsync(ct);
        var existingIndex = records.FindIndex(x =>
            string.Equals(x.Host, host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase));

        var existing = existingIndex >= 0 ? records[existingIndex] : null;
        var merged = new LastTaskContextRecord
        {
            Host = host,
            Username = username,
            ScopeKey = LastTaskContextRecord.BuildScopeKey(host, username),
            RootDirectory = MergeOptional(existing?.RootDirectory, record.RootDirectory),
            TaskId = MergeOptional(existing?.TaskId, record.TaskId),
            RemoteWorkDir = MergeOptional(existing?.RemoteWorkDir, record.RemoteWorkDir),
            CurrentTaskFilesPath = MergeOptional(existing?.CurrentTaskFilesPath, record.CurrentTaskFilesPath),
            LastUsedAt = record.LastUsedAt == default ? DateTimeOffset.UtcNow : record.LastUsedAt,
        };

        if (existingIndex >= 0)
            records[existingIndex] = merged;
        else
            records.Add(merged);

        await WriteAllAsync(records, ct);
    }

    private static string? MergeOptional(string? existing, string? incoming)
        => string.IsNullOrWhiteSpace(incoming) ? existing : incoming.Trim();

    private async Task<List<LastTaskContextRecord>> ReadAllAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
            return new List<LastTaskContextRecord>();

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, ct);
            if (string.IsNullOrWhiteSpace(json))
                return new List<LastTaskContextRecord>();

            return JsonSerializer.Deserialize<List<LastTaskContextRecord>>(json, JsonOptions) ?? new List<LastTaskContextRecord>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger?.Warning($"Failed to read last task contexts file '{_filePath}': {ex.Message}");
            return new List<LastTaskContextRecord>();
        }
    }

    private async Task WriteAllAsync(List<LastTaskContextRecord> records, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var normalized = records
            .Where(x => !string.IsNullOrWhiteSpace(x.Host) && !string.IsNullOrWhiteSpace(x.Username))
            .GroupBy(x => LastTaskContextRecord.BuildScopeKey(x.Host, x.Username), StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(x => x.LastUsedAt).First())
            .OrderByDescending(x => x.LastUsedAt)
            .ToList();

        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        await File.WriteAllTextAsync(_filePath, json, ct);
    }
}
