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
}
