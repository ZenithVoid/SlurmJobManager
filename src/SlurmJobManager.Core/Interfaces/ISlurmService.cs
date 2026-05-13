using SlurmJobManager.Core.Models;

namespace SlurmJobManager.Core.Interfaces;

/// <summary>High-level Slurm operations (submit, query, cancel).</summary>
public interface ISlurmService
{
    /// <summary>Submits an sbatch script from a remote path and returns the Slurm Job ID.</summary>
    Task<long> SubmitSbatchAsync(string remoteScriptPath, string? remoteWorkDir = null, CancellationToken ct = default);

    /// <summary>Returns the current status of a single job.</summary>
    Task<SlurmJobStatus?> GetJobStatusAsync(long jobId, CancellationToken ct = default);

    /// <summary>Returns all currently queued/running jobs for the specified user.</summary>
    Task<IReadOnlyList<SlurmJobStatus>> GetUserJobsAsync(string username, CancellationToken ct = default);

    /// <summary>Returns all currently queued/running jobs across all users.</summary>
    Task<IReadOnlyList<SlurmJobStatus>> GetAllJobsAsync(CancellationToken ct = default);

    /// <summary>Returns recent historical jobs for the specified user (typically backed by sacct).</summary>
    Task<IReadOnlyList<SlurmJobStatus>> GetUserJobHistoryAsync(string username, int maxEntries = 100, CancellationToken ct = default);

    /// <summary>Returns accounting status for a single job ID (typically backed by sacct -j).</summary>
    Task<SlurmJobStatus?> GetJobAccountingStatusAsync(long jobId, CancellationToken ct = default);

    /// <summary>Cancels a running or pending job.</summary>
    Task CancelJobAsync(long jobId, CancellationToken ct = default);
}
