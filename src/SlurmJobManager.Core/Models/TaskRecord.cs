namespace SlurmJobManager.Core.Models;

/// <summary>Represents a local task record stored under Root/TaskId.</summary>
public class TaskRecord
{
    public string TaskId { get; set; } = string.Empty;

    /// <summary>Local root directory where all task data is persisted.</summary>
    public string LocalRootDirectory { get; set; } = string.Empty;

    /// <summary>Remote working directory on the cluster.</summary>
    public string RemoteWorkDirectory { get; set; } = string.Empty;

    /// <summary>Slurm Job ID returned after a successful sbatch submission.</summary>
    public long? SlurmJobId { get; set; }

    /// <summary>Name of the parameter template file used for this task.</summary>
    public string? TemplateFileName { get; set; }

    /// <summary>Arbitrary key/value parameters for the sbatch script.</summary>
    public Dictionary<string, string> Parameters { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string? Notes { get; set; }
}
