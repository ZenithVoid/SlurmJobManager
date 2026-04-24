using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Models;

namespace SlurmJobManager.Infrastructure.Logs;

/// <summary>
/// Implements <see cref="ILogChunkService"/> by running <c>wc -l</c> and
/// <c>sed -n</c> over SSH.  The full file is never transferred.
/// </summary>
public sealed class SshLogChunkService : ILogChunkService
{
    private readonly ISshClientService _ssh;

    public SshLogChunkService(ISshClientService ssh)
        => _ssh = ssh ?? throw new ArgumentNullException(nameof(ssh));

    /// <inheritdoc/>
    public async Task<LogChunkResult> GetLatestChunkAsync(
        LogChunkRequest request, CancellationToken ct = default)
    {
        var total = await GetTotalLinesAsync(request.RemoteFilePath, ct);
        if (total == 0) return EmptyResult(0);

        var start = Math.Max(1, total - request.ChunkSize + 1);
        var lines = await ReadLinesAsync(request.RemoteFilePath, start, total, ct);
        return new LogChunkResult
        {
            Lines = lines,
            StartLine = start,
            EndLine = total,
            TotalLines = total,
        };
    }

    /// <inheritdoc/>
    public async Task<LogChunkResult> GetOlderChunkAsync(
        LogChunkRequest request, CancellationToken ct = default)
    {
        if (request.AnchorLine <= 1) return EmptyResult(await GetTotalLinesAsync(request.RemoteFilePath, ct));

        var total = await GetTotalLinesAsync(request.RemoteFilePath, ct);
        var end   = Math.Max(1, request.AnchorLine - 1);
        var start = Math.Max(1, end - request.ChunkSize + 1);
        var lines = await ReadLinesAsync(request.RemoteFilePath, start, end, ct);
        return new LogChunkResult
        {
            Lines = lines,
            StartLine = start,
            EndLine = end,
            TotalLines = total,
        };
    }

    /// <inheritdoc/>
    public async Task<LogChunkResult> GetNewerChunkAsync(
        LogChunkRequest request, CancellationToken ct = default)
    {
        var total = await GetTotalLinesAsync(request.RemoteFilePath, ct);
        var start = request.AnchorLine + 1;
        if (start > total) return EmptyResult(total);

        var end   = Math.Min(total, start + request.ChunkSize - 1);
        var lines = await ReadLinesAsync(request.RemoteFilePath, start, end, ct);
        return new LogChunkResult
        {
            Lines = lines,
            StartLine = start,
            EndLine = end,
            TotalLines = total,
        };
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<long> GetTotalLinesAsync(string remotePath, CancellationToken ct)
    {
        var (stdout, _, _) = await _ssh.ExecuteAsync(
            $"wc -l < {ShellEscape(remotePath)} 2>/dev/null || echo 0", ct);
        return long.TryParse(stdout.Trim(), out var n) ? n : 0;
    }

    private async Task<IReadOnlyList<string>> ReadLinesAsync(
        string remotePath, long start, long end, CancellationToken ct)
    {
        var (stdout, _, _) = await _ssh.ExecuteAsync(
            $"sed -n '{start},{end}p' {ShellEscape(remotePath)} 2>/dev/null", ct);

        var lines = stdout.Split('\n');
        // Remove the trailing empty string that results from a trailing newline
        return lines.Length > 0 && lines[^1] == string.Empty ? lines[..^1] : lines;
    }

    private static LogChunkResult EmptyResult(long total) => new()
    {
        Lines = Array.Empty<string>(),
        StartLine = 0,
        EndLine = 0,
        TotalLines = total,
    };

    /// <summary>Single-quote shell escaping for remote paths.</summary>
    private static string ShellEscape(string path)
        => "'" + path.Replace("'", "'\\''") + "'";
}
