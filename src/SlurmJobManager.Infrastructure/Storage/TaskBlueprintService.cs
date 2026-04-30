using System.Text.Json;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Models;
using SlurmJobManager.Core.Services;

namespace SlurmJobManager.Infrastructure.Storage;

/// <summary>
/// Stores task blueprints under &lt;AppBaseDirectory&gt;/Data/Blueprints with one JSON file per blueprint.
/// </summary>
public sealed class TaskBlueprintService : ITaskBlueprintService
{
    private const string BlueprintFileSuffix = ".blueprint.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _blueprintsDirectory;

    public TaskBlueprintService(string? blueprintsDirectory = null)
    {
        _blueprintsDirectory = string.IsNullOrWhiteSpace(blueprintsDirectory)
            ? LocalDataPaths.BlueprintsDirectory
            : blueprintsDirectory;

        Directory.CreateDirectory(_blueprintsDirectory);
    }

    public async Task SaveAsync(TaskBlueprintRecord blueprint, bool overwriteByName = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(blueprint);

        if (string.IsNullOrWhiteSpace(blueprint.Name))
            throw new InvalidOperationException("Blueprint name is required.");

        Directory.CreateDirectory(_blueprintsDirectory);

        var existingByName = await FindByNameAsync(blueprint.Name, ct);
        if (existingByName != null
            && !string.Equals(existingByName.BlueprintId, blueprint.BlueprintId, StringComparison.OrdinalIgnoreCase))
        {
            if (!overwriteByName)
                throw new InvalidOperationException("Blueprint name already exists.");

            blueprint.BlueprintId = existingByName.BlueprintId;
            blueprint.CreatedAt = existingByName.CreatedAt;
        }

        if (string.IsNullOrWhiteSpace(blueprint.BlueprintId))
            blueprint.BlueprintId = Guid.NewGuid().ToString("N");

        if (blueprint.CreatedAt == default)
            blueprint.CreatedAt = DateTime.UtcNow;

        blueprint.Name = blueprint.Name.Trim();
        blueprint.Description = blueprint.Description?.Trim() ?? string.Empty;
        blueprint.Version = TaskBlueprintRecord.CurrentVersion;
        blueprint.UpdatedAt = DateTime.UtcNow;

        var finalPath = GetBlueprintPath(blueprint.BlueprintId);
        var tempPath = finalPath + ".tmp";

        var json = JsonSerializer.Serialize(blueprint, SerializerOptions);
        await File.WriteAllTextAsync(tempPath, json, ct);
        File.Move(tempPath, finalPath, overwrite: true);
    }

    public async Task<IReadOnlyList<TaskBlueprintSummary>> ListAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_blueprintsDirectory);
        var result = new List<TaskBlueprintSummary>();

        foreach (var path in Directory.EnumerateFiles(_blueprintsDirectory, $"*{BlueprintFileSuffix}"))
        {
            var record = await ReadRecordSafelyAsync(path, ct);
            if (record == null) continue;

            result.Add(new TaskBlueprintSummary
            {
                BlueprintId = record.BlueprintId,
                Name = record.Name,
                Description = record.Description,
                Version = record.Version,
                CreatedAt = record.CreatedAt,
                UpdatedAt = record.UpdatedAt,
            });
        }

        return result
            .OrderByDescending(x => x.UpdatedAt)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<TaskBlueprintRecord?> LoadAsync(string blueprintId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(blueprintId))
            return null;

        var path = GetBlueprintPath(blueprintId);
        if (!File.Exists(path))
            return null;

        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<TaskBlueprintRecord>(json, SerializerOptions);
    }

    public Task<bool> DeleteAsync(string blueprintId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(blueprintId))
            return Task.FromResult(false);

        var path = GetBlueprintPath(blueprintId);
        if (!File.Exists(path))
            return Task.FromResult(false);

        File.Delete(path);
        return Task.FromResult(true);
    }

    public async Task<bool> ExistsByNameAsync(string blueprintName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(blueprintName))
            return false;

        return await FindByNameAsync(blueprintName, ct) != null;
    }

    private string GetBlueprintPath(string blueprintId)
        => Path.Combine(_blueprintsDirectory, $"{SanitizeId(blueprintId)}{BlueprintFileSuffix}");

    private static string SanitizeId(string blueprintId)
    {
        if (string.IsNullOrWhiteSpace(blueprintId))
            return Guid.NewGuid().ToString("N");

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(blueprintId.Where(ch => !invalidChars.Contains(ch)).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? Guid.NewGuid().ToString("N") : sanitized;
    }

    private async Task<TaskBlueprintRecord?> FindByNameAsync(string blueprintName, CancellationToken ct)
    {
        var target = blueprintName.Trim();
        if (target.Length == 0) return null;

        foreach (var path in Directory.EnumerateFiles(_blueprintsDirectory, $"*{BlueprintFileSuffix}"))
        {
            var record = await ReadRecordSafelyAsync(path, ct);
            if (record == null) continue;

            if (string.Equals(record.Name, target, StringComparison.OrdinalIgnoreCase))
                return record;
        }

        return null;
    }

    private static async Task<TaskBlueprintRecord?> ReadRecordSafelyAsync(string path, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize<TaskBlueprintRecord>(json, SerializerOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"[TaskBlueprintService] Failed to read blueprint '{path}': {ex.Message}");
            return null;
        }
    }
}
