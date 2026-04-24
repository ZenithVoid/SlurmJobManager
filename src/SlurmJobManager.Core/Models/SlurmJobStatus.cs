namespace SlurmJobManager.Core.Models;

/// <summary>Raw state string values returned by squeue/sacct.</summary>
public static class SlurmJobState
{
    public const string Pending = "PENDING";
    public const string Running = "RUNNING";
    public const string Completing = "COMPLETING";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";
    public const string Timeout = "TIMEOUT";
    public const string NodeFail = "NODE_FAIL";
    public const string Unknown = "UNKNOWN";
}

/// <summary>Status snapshot for a single Slurm job.</summary>
public class SlurmJobStatus
{
    public long JobId { get; set; }
    public string JobName { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public string State { get; set; } = SlurmJobState.Unknown;
    public string Partition { get; set; } = string.Empty;
    public int NumNodes { get; set; }
    public int NumCpus { get; set; }
    public string? NodeList { get; set; }
    public TimeSpan? RunTime { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? WorkDir { get; set; }
    public string? StdOut { get; set; }
    public string? StdErr { get; set; }
}
