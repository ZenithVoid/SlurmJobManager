using SlurmJobManager.Core.Models;

namespace SlurmJobManager.Core.Interfaces;

/// <summary>
/// Provides chunked (paged) access to potentially very large remote log files.
/// All methods operate over SSH so they never download the entire file at once.
/// </summary>
public interface ILogChunkService
{
    /// <summary>
    /// Returns the last <see cref="LogChunkRequest.ChunkSize"/> lines of the file.
    /// Useful as the first load or for a "jump to end / refresh" action.
    /// </summary>
    Task<LogChunkResult> GetLatestChunkAsync(LogChunkRequest request, CancellationToken ct = default);

    /// <summary>
    /// Returns <see cref="LogChunkRequest.ChunkSize"/> lines immediately before
    /// <see cref="LogChunkRequest.AnchorLine"/> (i.e. scrolling backwards / older).
    /// </summary>
    Task<LogChunkResult> GetOlderChunkAsync(LogChunkRequest request, CancellationToken ct = default);

    /// <summary>
    /// Returns <see cref="LogChunkRequest.ChunkSize"/> lines immediately after
    /// <see cref="LogChunkRequest.AnchorLine"/> (i.e. scrolling forwards / newer).
    /// </summary>
    Task<LogChunkResult> GetNewerChunkAsync(LogChunkRequest request, CancellationToken ct = default);
}
