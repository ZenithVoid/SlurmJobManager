using System.Text.Json;
using System.Text.Json.Serialization;
using SlurmPilot.Core.Interfaces;
using SlurmPilot.Core.Models;
using SlurmPilot.Core.Services;

namespace SlurmPilot.Infrastructure.Storage;

/// <summary>JSON-backed local storage for recent SSH connections.</summary>
public sealed class RecentConnectionService : IRecentConnectionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath = LocalDataPaths.RecentConnectionsFilePath;
    private readonly IAppLogger? _logger;

    public RecentConnectionService(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<RecentConnectionRecord>> GetRecentAsync(CancellationToken ct = default)
    {
        var records = await ReadAllAsync(ct);
        return records
            .OrderByDescending(x => x.LastUsedAt)
            .ToList();
    }

    public async Task AddOrUpdateAsync(RecentConnectionRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var records = await ReadAllAsync(ct);
        var existingIndex = records.FindIndex(x =>
            string.Equals(x.Host, record.Host, StringComparison.OrdinalIgnoreCase) &&
            x.Port == record.Port &&
            string.Equals(x.Username, record.Username, StringComparison.OrdinalIgnoreCase));

        var normalized = new RecentConnectionRecord
        {
            Host = record.Host.Trim(),
            Port = record.Port <= 0 ? 22 : record.Port,
            Username = record.Username.Trim(),
            Label = string.IsNullOrWhiteSpace(record.Label) ? null : record.Label.Trim(),
            LastUsedAt = record.LastUsedAt == default ? DateTimeOffset.UtcNow : record.LastUsedAt,
        };

        if (existingIndex >= 0)
            records[existingIndex] = normalized;
        else
            records.Add(normalized);

        await WriteAllAsync(records, ct);
    }

    public async Task RemoveAsync(string host, int port, string username, CancellationToken ct = default)
    {
        var records = await ReadAllAsync(ct);
        records.RemoveAll(x =>
            string.Equals(x.Host, host, StringComparison.OrdinalIgnoreCase) &&
            x.Port == port &&
            string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase));
        await WriteAllAsync(records, ct);
    }

    private async Task<List<RecentConnectionRecord>> ReadAllAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
            return new List<RecentConnectionRecord>();

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, ct);
            if (string.IsNullOrWhiteSpace(json))
                return new List<RecentConnectionRecord>();

            return JsonSerializer.Deserialize<List<RecentConnectionRecord>>(json, JsonOptions) ?? new List<RecentConnectionRecord>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger?.Warning($"Failed to read recent connections file '{_filePath}': {ex.Message}");
            return new List<RecentConnectionRecord>();
        }
    }

    private async Task WriteAllAsync(List<RecentConnectionRecord> records, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var sorted = records
            .Where(x => !string.IsNullOrWhiteSpace(x.Host) && !string.IsNullOrWhiteSpace(x.Username))
            .OrderByDescending(x => x.LastUsedAt)
            .Take(30)
            .ToList();

        var json = JsonSerializer.Serialize(sorted, JsonOptions);
        await File.WriteAllTextAsync(_filePath, json, ct);
    }
}
