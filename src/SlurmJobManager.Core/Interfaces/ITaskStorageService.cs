using SlurmJobManager.Core.Models;

namespace SlurmJobManager.Core.Interfaces;

/// <summary>Persists and loads task records under Root/{TaskId}/.</summary>
public interface ITaskStorageService
{
    /// <summary>
    /// Ensures the directory Root/{TaskId}/ exists and writes task.json.
    /// </summary>
    Task SaveAsync(TaskRecord task, CancellationToken ct = default);

    /// <summary>Loads task.json from Root/{TaskId}/.</summary>
    Task<TaskRecord?> LoadAsync(string rootDirectory, string taskId, CancellationToken ct = default);

    /// <summary>Lists all task IDs found under the given root directory.</summary>
    IEnumerable<string> ListTaskIds(string rootDirectory);

    /// <summary>Returns the full local path for a task: Root/{TaskId}/.</summary>
    string GetTaskDirectory(string rootDirectory, string taskId);

    // ── Multi-task workspace ─────────────────────────────────────────────────

    /// <summary>
    /// Saves <paramref name="workspace"/> as <c>tasks.manifest.json</c> under
    /// Root/{TaskId}/. Creates the directory if it does not exist.
    /// Uses a temporary-file + replace strategy to guard against partial writes.
    /// </summary>
    Task SaveWorkspaceAsync(TaskWorkspace workspace, CancellationToken ct = default);

    /// <summary>
    /// Loads the workspace manifest from Root/{TaskId}/tasks.manifest.json.
    /// Falls back to migrating a legacy <c>task.json</c> when the manifest is absent.
    /// Returns <c>null</c> when neither file exists.
    /// </summary>
    Task<TaskWorkspace?> LoadWorkspaceAsync(string rootDirectory, string taskId, CancellationToken ct = default);
}

