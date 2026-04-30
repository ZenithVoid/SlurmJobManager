namespace SlurmJobManager.Core.Models;

/// <summary>
/// A reusable snapshot of task editor data that can be materialized into a new TaskId.
/// </summary>
public sealed class TaskBlueprintRecord
{
    public const int CurrentVersion = 1;

    public string BlueprintId { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Version { get; set; } = CurrentVersion;

    public string? SourceTaskId { get; set; }
    public string? RootDirectory { get; set; }
    public string? RemoteWorkDirectory { get; set; }
    public string? ActiveTaskUnitName { get; set; }

    public List<TaskUnit> TaskUnits { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Lightweight metadata for blueprint list views.</summary>
public sealed class TaskBlueprintSummary
{
    public string BlueprintId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Version { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
