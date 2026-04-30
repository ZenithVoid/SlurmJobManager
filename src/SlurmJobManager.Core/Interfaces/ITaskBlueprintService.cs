using SlurmJobManager.Core.Models;

namespace SlurmJobManager.Core.Interfaces;

/// <summary>
/// Persists and loads reusable task blueprints from dedicated local storage.
/// </summary>
public interface ITaskBlueprintService
{
    Task SaveAsync(TaskBlueprintRecord blueprint, bool overwriteByName = false, CancellationToken ct = default);
    Task<IReadOnlyList<TaskBlueprintSummary>> ListAsync(CancellationToken ct = default);
    Task<TaskBlueprintRecord?> LoadAsync(string blueprintId, CancellationToken ct = default);
    Task<bool> DeleteAsync(string blueprintId, CancellationToken ct = default);
    Task<bool> ExistsByNameAsync(string blueprintName, CancellationToken ct = default);
}
