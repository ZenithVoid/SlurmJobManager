using SlurmPilot.Core.Models;

namespace SlurmPilot.Core.Interfaces;

/// <summary>
/// Persists and loads reusable task blueprints from dedicated local storage.
/// </summary>
public interface ITaskBlueprintService
{
    Task SaveAsync(TaskBlueprintRecord blueprint, TaskBlueprintScope scope, bool overwriteByName = false, CancellationToken ct = default);
    Task<IReadOnlyList<TaskBlueprintSummary>> ListAsync(TaskBlueprintScope scope, CancellationToken ct = default);
    Task<TaskBlueprintRecord?> LoadAsync(string blueprintId, TaskBlueprintScope scope, CancellationToken ct = default);
    Task<bool> DeleteAsync(string blueprintId, TaskBlueprintScope scope, CancellationToken ct = default);
    Task<bool> ExistsByNameAsync(string blueprintName, TaskBlueprintScope scope, CancellationToken ct = default);
}
