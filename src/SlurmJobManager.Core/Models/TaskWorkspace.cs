namespace SlurmJobManager.Core.Models;

/// <summary>
/// Represents the full workspace under a single TaskId directory.
/// Replaces the legacy single-task <see cref="TaskRecord"/> for new workspaces
/// while remaining backward-compatible (old task.json is migrated as Tasks[0]).
/// </summary>
public class TaskWorkspace
{
    public string TaskId   { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;

    /// <summary>All task units defined under this TaskId directory.</summary>
    public List<TaskUnit> Tasks { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A single submittable task unit within a <see cref="TaskWorkspace"/>.</summary>
public class TaskUnit
{
    /// <summary>Human-readable name; auto-generated if empty.</summary>
    public string TaskName { get; set; } = string.Empty;

    /// <summary>When false the unit is skipped during "submit all" operations.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Remote working directory (overrides workspace default when set).</summary>
    public string? RemoteWorkDirectory { get; set; }

    /// <summary>Slurm Job ID from the most recent submission of this unit.</summary>
    public long? SlurmJobId { get; set; }

    /// <summary>Custom sbatch script template (falls back to workspace default when null).</summary>
    public string? SbatchTemplate { get; set; }

    /// <summary>Programs to run (ordered).</summary>
    public List<ProgramEntry> ProgramEntries { get; set; } = new();

    /// <summary>Parameter files to reference during submission.</summary>
    public List<ParameterFileEntry> ParameterFiles { get; set; } = new();

    /// <summary>Shell command lines to execute (ordered).</summary>
    public List<CommandEntry> CommandEntries { get; set; } = new();

    /// <summary>Arbitrary key/value extra sbatch parameters.</summary>
    public Dictionary<string, string> ExtraParameters { get; set; } = new();
}

/// <summary>A single program (executable) entry within a <see cref="TaskUnit"/>.</summary>
public class ProgramEntry
{
    public string  ProgramPath   { get; set; } = string.Empty;
    public string? ArgsTemplate  { get; set; }
    public int     Order         { get; set; }
}

/// <summary>A single parameter file entry within a <see cref="TaskUnit"/>.</summary>
public class ParameterFileEntry
{
    public string  FilePath { get; set; } = string.Empty;
    public string? Alias    { get; set; }
    public bool    IsPinned { get; set; }
}

/// <summary>A single command-line entry within a <see cref="TaskUnit"/>.</summary>
public class CommandEntry
{
    public string  CommandLine  { get; set; } = string.Empty;
    public string? Description  { get; set; }
    public int     Order        { get; set; }
}
