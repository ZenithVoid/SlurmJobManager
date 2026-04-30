using System.Globalization;
using System.Text.RegularExpressions;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Models;
using SlurmJobManager.Infrastructure.Resilience;

namespace SlurmJobManager.Infrastructure.Ssh;

/// <summary>
/// Implements Slurm job operations by executing remote commands via SSH.
/// Transient failures are retried with exponential back-off.
/// </summary>
public sealed class SlurmService : ISlurmService
{
    private readonly ISshClientService _ssh;
    private readonly AppSettings       _settings;
    private readonly IAppLogger?       _logger;

    public SlurmService(ISshClientService ssh, AppSettings? settings = null, IAppLogger? logger = null)
    {
        _ssh      = ssh      ?? throw new ArgumentNullException(nameof(ssh));
        _settings = settings ?? new AppSettings();
        _logger   = logger;
    }

    /// <inheritdoc/>
    public async Task<long> SubmitSbatchAsync(
        string localScriptPath,
        string? remoteWorkDir = null,
        CancellationToken ct = default)
    {
        _logger?.Info($"Submitting sbatch script: {localScriptPath}");

        // Upload the script to a user-private temp location to avoid world-readable /tmp
        var remoteScript = $"$HOME/.sjm_tmp/sjm_{Guid.NewGuid():N}.sh";
        await _ssh.ExecuteAsync("mkdir -p $HOME/.sjm_tmp && chmod 700 $HOME/.sjm_tmp", ct);
        await _ssh.UploadFileAsync(localScriptPath, remoteScript, ct);

        var cdPart  = string.IsNullOrEmpty(remoteWorkDir) ? string.Empty : $"cd {remoteWorkDir} && ";
        var command = $"{cdPart}sbatch {remoteScript}";

        var (stdout, stderr, exitCode) = await _ssh.ExecuteAsync(command, ct);

        if (exitCode != 0)
        {
            _logger?.Error($"sbatch failed (exit {exitCode}): {stderr.Trim()}");
            throw new InvalidOperationException(
                $"sbatch failed (exit {exitCode}): {stderr.Trim()}");
        }

        // Parse "Submitted batch job 12345"
        var match = Regex.Match(stdout, @"Submitted batch job (\d+)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            _logger?.Error($"Could not parse job id from sbatch output: {stdout.Trim()}");
            throw new InvalidOperationException(
                $"Could not parse job id from sbatch output: {stdout.Trim()}");
        }

        var jobId = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        _logger?.Info($"sbatch submitted successfully. Job ID = {jobId}");
        return jobId;
    }

    /// <inheritdoc/>
    public async Task<SlurmJobStatus?> GetJobStatusAsync(long jobId, CancellationToken ct = default)
    {
        var result = await RetryHelper.ExecuteAsync(
            async token =>
            {
                var (stdout, _, _) = await _ssh.ExecuteAsync(
                    $"squeue --job {jobId} --noheader --format=\"%i|%j|%u|%T|%P|%D|%C|%N|%M|%S\"", token);
                var line = stdout.Trim();
                return string.IsNullOrEmpty(line) ? null : ParseSqueueLine(line);
            },
            _settings, _logger, $"GetJobStatus({jobId})", ct);

        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SlurmJobStatus>> GetUserJobsAsync(
        string username, CancellationToken ct = default)
    {
        return await RetryHelper.ExecuteAsync(
            async token =>
            {
                var (stdout, _, _) = await _ssh.ExecuteAsync(
                    $"squeue --user {username} --noheader --format=\"%i|%j|%u|%T|%P|%D|%C|%N|%M|%S\"", token);

                var results = new List<SlurmJobStatus>();
                foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var status = ParseSqueueLine(line.Trim());
                    if (status is not null) results.Add(status);
                }
                return (IReadOnlyList<SlurmJobStatus>)results;
            },
            _settings, _logger, $"GetUserJobs({username})", ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SlurmJobStatus>> GetAllJobsAsync(CancellationToken ct = default)
    {
        return await RetryHelper.ExecuteAsync(
            async token =>
            {
                var (stdout, _, _) = await _ssh.ExecuteAsync(
                    "squeue --noheader --format=\"%i|%j|%u|%T|%P|%D|%C|%N|%M|%S\"", token);

                var results = new List<SlurmJobStatus>();
                foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var status = ParseSqueueLine(line.Trim());
                    if (status is not null) results.Add(status);
                }
                return (IReadOnlyList<SlurmJobStatus>)results;
            },
            _settings, _logger, "GetAllJobs()", ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SlurmJobStatus>> GetUserJobHistoryAsync(
        string username,
        int maxEntries = 100,
        CancellationToken ct = default)
    {
        var safeMaxEntries = Math.Max(1, maxEntries);
        return await RetryHelper.ExecuteAsync(
            async token =>
            {
                var command = string.Join(" ", new[]
                {
                    $"sacct -X -u {username}",
                    "--starttime now-7days",
                    "--noheader --parsable2",
                    "--format=\"JobIDRaw,JobName,User,State,Partition,NodeList,Start,End,Elapsed,ExitCode,Reason\"",
                    "--sort=-Start",
                    $"| head -n {safeMaxEntries}"
                });

                var (stdout, _, _) = await _ssh.ExecuteAsync(command, token);
                var results = new List<SlurmJobStatus>();
                foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var status = ParseSacctLine(line.Trim());
                    if (status is not null) results.Add(status);
                }

                return (IReadOnlyList<SlurmJobStatus>)results;
            },
            _settings, _logger, $"GetUserJobHistory({username})", ct);
    }

    /// <inheritdoc/>
    public async Task CancelJobAsync(long jobId, CancellationToken ct = default)
    {
        var (_, stderr, exitCode) = await _ssh.ExecuteAsync($"scancel {jobId}", ct);
        if (exitCode != 0)
            throw new InvalidOperationException($"scancel failed (exit {exitCode}): {stderr.Trim()}");
        _logger?.Info($"Job {jobId} cancelled.");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static SlurmJobStatus? ParseSqueueLine(string line)
    {
        // Format: jobId|name|user|state|partition|nodes|cpus|nodeList|runTime|startTime
        var parts = line.Split('|');
        if (parts.Length < 6) return null;

        if (!long.TryParse(parts[0].Trim(), out var jobId)) return null;

        return new SlurmJobStatus
        {
            JobId     = jobId,
            JobName   = parts[1].Trim(),
            User      = parts[2].Trim(),
            State     = parts[3].Trim(),
            Partition = parts[4].Trim(),
            NumNodes  = int.TryParse(parts[5].Trim(), out var n) ? n : 0,
            NumCpus   = parts.Length > 6 && int.TryParse(parts[6].Trim(), out var c) ? c : 0,
            NodeList  = parts.Length > 7 ? parts[7].Trim() : null,
            RunTime   = parts.Length > 8 ? ParseSlurmElapsed(parts[8]) : null,
            StartTime = parts.Length > 9 ? ParseSlurmDateTime(parts[9]) : null,
            IsHistorical = false,
        };
    }

    private static SlurmJobStatus? ParseSacctLine(string line)
    {
        // Format: jobIdRaw|jobName|user|state|partition|nodeList|start|end|elapsed|exitCode|reason
        var parts = line.Split('|');
        if (parts.Length < 4) return null;

        var rawJobId = parts[0].Trim();
        if (!TryParsePrimaryJobId(rawJobId, out var jobId)) return null;

        var state = GetValue(parts, 3);
        if (state.Contains('+', StringComparison.Ordinal))
            state = state[..state.IndexOf('+')];

        return new SlurmJobStatus
        {
            JobId       = jobId,
            JobName     = GetValue(parts, 1),
            User        = GetValue(parts, 2),
            State       = string.IsNullOrWhiteSpace(state) ? SlurmJobState.Unknown : state,
            Partition   = GetValue(parts, 4),
            NodeList    = NormalizeNullable(GetValue(parts, 5)),
            StartTime   = ParseSlurmDateTime(GetValue(parts, 6)),
            EndTime     = ParseSlurmDateTime(GetValue(parts, 7)),
            RunTime     = ParseSlurmElapsed(GetValue(parts, 8)),
            ExitCode    = NormalizeNullable(GetValue(parts, 9)),
            Reason      = NormalizeNullable(GetValue(parts, 10)),
            IsHistorical = true,
        };
    }

    private static string GetValue(string[] parts, int index)
        => index < parts.Length ? parts[index].Trim() : string.Empty;

    private static bool TryParsePrimaryJobId(string rawJobId, out long jobId)
    {
        jobId = 0;
        var primary = rawJobId.Split('.', StringSplitOptions.RemoveEmptyEntries)[0];
        return long.TryParse(primary, NumberStyles.Integer, CultureInfo.InvariantCulture, out jobId);
    }

    private static DateTime? ParseSlurmDateTime(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim();
        if (value is "Unknown" or "N/A" or "None" or "0") return null;

        var styles = DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, styles, out var parsed) ||
            DateTime.TryParse(value, CultureInfo.CurrentCulture, styles, out parsed))
            return parsed;

        return null;
    }

    private static TimeSpan? ParseSlurmElapsed(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim();
        if (value is "Unknown" or "N/A" or "None" or "0") return null;

        var daySplit = value.Split('-', 2, StringSplitOptions.TrimEntries);
        if (daySplit.Length == 2
            && int.TryParse(daySplit[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var days)
            && TimeSpan.TryParse(daySplit[1], CultureInfo.InvariantCulture, out var dayTime))
            return dayTime + TimeSpan.FromDays(days);

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var elapsed))
            return elapsed;

        if (TimeSpan.TryParse(value, CultureInfo.CurrentCulture, out elapsed))
            return elapsed;

        return null;
    }

    private static string? NormalizeNullable(string value)
        => string.IsNullOrWhiteSpace(value) || value is "Unknown" or "N/A" or "None"
            ? null
            : value;
}
