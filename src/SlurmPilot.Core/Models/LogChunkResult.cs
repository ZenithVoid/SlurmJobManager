namespace SlurmPilot.Core.Models;

/// <summary>Chunk of lines returned from a remote log file.</summary>
public class LogChunkResult
{
    /// <summary>The lines in this chunk (in file order).</summary>
    public IReadOnlyList<string> Lines { get; set; } = Array.Empty<string>();

    /// <summary>1-based index of the first line in the chunk within the file.</summary>
    public long StartLine { get; set; }

    /// <summary>1-based index of the last line in the chunk within the file.</summary>
    public long EndLine { get; set; }

    /// <summary>Total number of lines in the remote file at the time of the request.</summary>
    public long TotalLines { get; set; }

    /// <summary>True when StartLine == 1 (beginning of file reached).</summary>
    public bool IsAtStart => StartLine <= 1;

    /// <summary>True when EndLine >= TotalLines (end of file reached).</summary>
    public bool IsAtEnd => EndLine >= TotalLines;
}
