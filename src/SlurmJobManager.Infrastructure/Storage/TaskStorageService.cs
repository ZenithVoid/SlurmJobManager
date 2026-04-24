using System.Text.Json;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Models;

namespace SlurmJobManager.Infrastructure.Storage;

/// <summary>
/// Persists <see cref="TaskRecord"/> instances as JSON under Root/{TaskId}/task.json.
/// </summary>
public sealed class TaskStorageService : ITaskStorageService
{
    private const string TaskFileName = "task.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <inheritdoc/>
    public string GetTaskDirectory(string rootDirectory, string taskId)
        => Path.Combine(rootDirectory, taskId);

    /// <inheritdoc/>
    public async Task SaveAsync(TaskRecord task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        task.UpdatedAt = DateTime.UtcNow;
        var dir = GetTaskDirectory(task.LocalRootDirectory, task.TaskId);
        Directory.CreateDirectory(dir);

        var filePath = Path.Combine(dir, TaskFileName);
        var json = JsonSerializer.Serialize(task, SerializerOptions);
        await File.WriteAllTextAsync(filePath, json, ct);
    }

    /// <inheritdoc/>
    public async Task<TaskRecord?> LoadAsync(string rootDirectory, string taskId, CancellationToken ct = default)
    {
        var filePath = Path.Combine(GetTaskDirectory(rootDirectory, taskId), TaskFileName);
        if (!File.Exists(filePath)) return null;

        var json = await File.ReadAllTextAsync(filePath, ct);
        return JsonSerializer.Deserialize<TaskRecord>(json, SerializerOptions);
    }

    /// <inheritdoc/>
    public IEnumerable<string> ListTaskIds(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory))
            return Enumerable.Empty<string>();

        return Directory.EnumerateDirectories(rootDirectory)
            .Where(d => File.Exists(Path.Combine(d, TaskFileName)))
            .Select(d => Path.GetFileName(d)!)
            .OrderBy(id => id);
    }
}
