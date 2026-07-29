using System.Text.Json;
using SlurmPilot.Core.Interfaces;
using SlurmPilot.Core.Models;
using SlurmPilot.Core.Services;

namespace SlurmPilot.Infrastructure.Storage;

/// <summary>
/// Stores task blueprints under &lt;AppBaseDirectory&gt;/Data/blueprints grouped by host/user scope.
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

    public async Task SaveAsync(TaskBlueprintRecord blueprint, TaskBlueprintScope scope, bool overwriteByName = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        var normalizedScope = NormalizeScope(scope);

        if (string.IsNullOrWhiteSpace(blueprint.Name))
            throw new InvalidOperationException("Blueprint name is required.");

        var scopeDirectory = GetScopeDirectory(normalizedScope.ScopeKey);
        Directory.CreateDirectory(scopeDirectory);

        var existingByName = await FindByNameAsync(blueprint.Name, scopeDirectory, ct);
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
        blueprint.ScopeHostOrAddress = normalizedScope.HostOrAddress;
        blueprint.ScopeUsername = normalizedScope.Username;
        blueprint.ScopeKey = normalizedScope.ScopeKey;
        blueprint.UpdatedAt = DateTime.UtcNow;

        var finalPath = GetBlueprintPath(scopeDirectory, blueprint.BlueprintId);
        var tempPath = finalPath + ".tmp";

        var json = JsonSerializer.Serialize(blueprint, SerializerOptions);
        await File.WriteAllTextAsync(tempPath, json, ct);
        File.Move(tempPath, finalPath, overwrite: true);
    }

    public async Task<IReadOnlyList<TaskBlueprintSummary>> ListAsync(TaskBlueprintScope scope, CancellationToken ct = default)
    {
        var normalizedScope = NormalizeScope(scope);
        var scopeDirectory = GetScopeDirectory(normalizedScope.ScopeKey);
        Directory.CreateDirectory(scopeDirectory);
        var result = new List<TaskBlueprintSummary>();

        foreach (var path in Directory.EnumerateFiles(scopeDirectory, $"*{BlueprintFileSuffix}"))
        {
            var record = await ReadRecordSafelyAsync(path, ct);
            if (record == null) continue;

            result.Add(new TaskBlueprintSummary
            {
                BlueprintId = record.BlueprintId,
                Name = record.Name,
                Description = record.Description,
                Version = record.Version,
                ScopeHostOrAddress = record.ScopeHostOrAddress,
                ScopeUsername = record.ScopeUsername,
                ScopeKey = record.ScopeKey,
                CreatedAt = record.CreatedAt,
                UpdatedAt = record.UpdatedAt,
            });
        }

        return result
            .OrderByDescending(x => x.UpdatedAt)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<TaskBlueprintRecord?> LoadAsync(string blueprintId, TaskBlueprintScope scope, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(blueprintId))
            return null;

        var normalizedScope = NormalizeScope(scope);
        var path = GetBlueprintPath(GetScopeDirectory(normalizedScope.ScopeKey), blueprintId);
        if (!File.Exists(path))
            return null;

        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<TaskBlueprintRecord>(json, SerializerOptions);
    }

    public Task<bool> DeleteAsync(string blueprintId, TaskBlueprintScope scope, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(blueprintId))
            return Task.FromResult(false);

        var normalizedScope = NormalizeScope(scope);
        var path = GetBlueprintPath(GetScopeDirectory(normalizedScope.ScopeKey), blueprintId);
        if (!File.Exists(path))
            return Task.FromResult(false);

        File.Delete(path);
        return Task.FromResult(true);
    }

    public async Task<bool> ExistsByNameAsync(string blueprintName, TaskBlueprintScope scope, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(blueprintName))
            return false;

        var normalizedScope = NormalizeScope(scope);
        var scopeDirectory = GetScopeDirectory(normalizedScope.ScopeKey);
        Directory.CreateDirectory(scopeDirectory);
        return await FindByNameAsync(blueprintName, scopeDirectory, ct) != null;
    }

    private string GetScopeDirectory(string scopeKey)
        => Path.Combine(_blueprintsDirectory, SanitizeId(scopeKey));

    private static (string HostOrAddress, string Username, string ScopeKey) NormalizeScope(TaskBlueprintScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var host = scope.HostOrAddress?.Trim() ?? string.Empty;
        var username = scope.Username?.Trim() ?? string.Empty;
        if (host.Length == 0 || username.Length == 0)
        {
            if (host.Length == 0 && username.Length == 0)
                throw new InvalidOperationException("Blueprint scope host and username are required.");
            if (host.Length == 0)
                throw new InvalidOperationException("Blueprint scope host is required.");
            throw new InvalidOperationException("Blueprint scope username is required.");
        }

        return (host, username, TaskBlueprintScope.BuildScopeKey(host, username));
    }

    private static string GetBlueprintPath(string scopeDirectory, string blueprintId)
        => Path.Combine(scopeDirectory, $"{SanitizeId(blueprintId)}{BlueprintFileSuffix}");

    private static string SanitizeId(string blueprintId)
    {
        if (string.IsNullOrWhiteSpace(blueprintId))
            return Guid.NewGuid().ToString("N");

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(blueprintId.Where(ch => !invalidChars.Contains(ch)).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? Guid.NewGuid().ToString("N") : sanitized;
    }

    private async Task<TaskBlueprintRecord?> FindByNameAsync(string blueprintName, string scopeDirectory, CancellationToken ct)
    {
        var target = blueprintName.Trim();
        if (target.Length == 0) return null;

        foreach (var path in Directory.EnumerateFiles(scopeDirectory, $"*{BlueprintFileSuffix}"))
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
