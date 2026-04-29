using System.Text.Json;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Models;

namespace SlurmJobManager.Infrastructure.Storage;

/// <summary>
/// Persists <see cref="TaskRecord"/> and <see cref="TaskWorkspace"/> instances
/// under Root/{TaskId}/.
/// </summary>
public sealed class TaskStorageService : ITaskStorageService
{
    private const string TaskFileName      = "task.json";
    private const string ManifestFileName  = "tasks.manifest.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <inheritdoc/>
    public string GetTaskDirectory(string rootDirectory, string taskId)
        => Path.Combine(rootDirectory, taskId);

    // ── Legacy TaskRecord ────────────────────────────────────────────────────

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

    // ── Multi-task workspace ─────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task SaveWorkspaceAsync(TaskWorkspace workspace, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        workspace.UpdatedAt = DateTime.UtcNow;
        var dir = GetTaskDirectory(workspace.RootPath, workspace.TaskId);
        Directory.CreateDirectory(dir);

        var finalPath = Path.Combine(dir, ManifestFileName);
        var tmpPath   = finalPath + ".tmp";

        var json = JsonSerializer.Serialize(workspace, SerializerOptions);

        // Write to temp file first, then atomically replace to guard against partial writes
        await File.WriteAllTextAsync(tmpPath, json, ct);
        File.Move(tmpPath, finalPath, overwrite: true);
    }

    /// <inheritdoc/>
    public async Task<TaskWorkspace?> LoadWorkspaceAsync(string rootDirectory, string taskId, CancellationToken ct = default)
    {
        var dir          = GetTaskDirectory(rootDirectory, taskId);
        var manifestPath = Path.Combine(dir, ManifestFileName);

        // Prefer the new manifest format
        if (File.Exists(manifestPath))
        {
            var json = await File.ReadAllTextAsync(manifestPath, ct);
            return JsonSerializer.Deserialize<TaskWorkspace>(json, SerializerOptions);
        }

        // Fall back: migrate legacy task.json → Tasks[0]
        var legacyPath = Path.Combine(dir, TaskFileName);
        if (!File.Exists(legacyPath)) return null;

        var legacyJson  = await File.ReadAllTextAsync(legacyPath, ct);
        var legacyRecord = JsonSerializer.Deserialize<TaskRecord>(legacyJson, SerializerOptions);
        if (legacyRecord is null) return null;

        return MigrateLegacyRecord(legacyRecord, rootDirectory, taskId);
    }

    // ── Migration helper ─────────────────────────────────────────────────────

    private static TaskWorkspace MigrateLegacyRecord(TaskRecord r, string rootDirectory, string taskId)
    {
        var unit = new TaskUnit
        {
            TaskName            = taskId,
            Enabled             = true,
            RemoteWorkDirectory = r.RemoteWorkDirectory,
            SlurmJobId          = r.SlurmJobId,
            // TaskRecord uses "Parameters" for sbatch key/values; TaskUnit calls the same
            // concept "ExtraParameters" to distinguish it from the structured program/file entries.
            ExtraParameters     = new Dictionary<string, string>(r.Parameters),
        };

        if (!string.IsNullOrWhiteSpace(r.TemplateFileName))
            unit.ParameterFiles.Add(new ParameterFileEntry { FilePath = r.TemplateFileName });

        return new TaskWorkspace
        {
            TaskId    = taskId,
            RootPath  = rootDirectory,
            Tasks     = new List<TaskUnit> { unit },
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt,
        };
    }
}

