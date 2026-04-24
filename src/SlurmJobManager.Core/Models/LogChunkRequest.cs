namespace SlurmJobManager.Core.Models;

/// <summary>Describes a request for a chunk of lines from a remote log file.</summary>
public class LogChunkRequest
{
    /// <summary>Absolute path of the remote log file.</summary>
    public string RemoteFilePath { get; set; } = string.Empty;

    /// <summary>
    /// 1-based anchor line number.
    /// For GetLatestChunk this is ignored (server determines tail).
    /// For GetOlderChunk this is the first line of the current chunk.
    /// For GetNewerChunk this is the last line of the current chunk.
    /// </summary>
    public long AnchorLine { get; set; }

    /// <summary>Number of lines to return per chunk.</summary>
    public int ChunkSize { get; set; } = 500;
}
