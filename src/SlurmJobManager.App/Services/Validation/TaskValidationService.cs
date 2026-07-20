using System.Text.RegularExpressions;
using System.Windows;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Models;

namespace SlurmJobManager.App.Services.Validation;

public sealed class TaskValidationService : ITaskValidationService
{
    private static readonly Regex PositiveIntegerRegex = new(@"^\d+$", RegexOptions.Compiled);
    private static readonly Regex MpiLauncherRegex = new(@"(^|\s)(\S*/)?mpi(run|exec)(\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly ISshClientService _ssh;

    public TaskValidationService(ISshClientService ssh)
    {
        _ssh = ssh;
    }

    public async Task<TaskValidationResult> ValidateAsync(
        TaskValidationContext context,
        TaskValidationOperation operation,
        CancellationToken ct = default)
    {
        var issues = new List<TaskValidationIssue>();

        var workDir = NormalizeRemotePath(context.EffectiveWorkDirectory);
        var appPath = NormalizeRemotePath(context.AppPath);
        var sbatchOptions = context.SbatchOptions ?? new SbatchJobOptions();
        var parameterFiles = context.ParameterFiles
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => NormalizeRemotePath(path))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var commands = context.Commands
            .Select((command, index) => new CommandValidationItem
            {
                Order = command.Order > 0 ? command.Order : index + 1,
                CommandLine = command.CommandLine?.Trim() ?? string.Empty,
                ProgramPath = NormalizeRemotePath(command.ProgramPath),
                MpirunPath = NormalizeRemotePath(command.MpirunPath),
                ParameterFiles = command.ParameterFiles
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => NormalizeRemotePath(path))
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
            })
            .ToList();
        var hasCommandPayload = commands.Any(HasCommandPayload);

        if (string.IsNullOrWhiteSpace(context.RootDirectory))
        {
            issues.Add(CreateIssue(
                "root.required",
                "Task.Validation.RootRequiredTitle",
                "Task.Validation.RootRequiredDetail",
                TaskValidationSeverity.Error,
                isBlocking: true));
        }

        if (string.IsNullOrWhiteSpace(context.TaskId))
        {
            issues.Add(CreateIssue(
                "taskid.required",
                "Task.Validation.TaskIdRequiredTitle",
                "Task.Validation.TaskIdRequiredDetail",
                TaskValidationSeverity.Error,
                isBlocking: true));
        }

        if (!context.IsSshConnected)
        {
            issues.Add(CreateIssue(
                "ssh.required",
                "Task.Validation.ConnectionRequiredTitle",
                "Task.Validation.ConnectionRequiredDetail",
                TaskValidationSeverity.Error,
                isBlocking: true));
            return new TaskValidationResult { Issues = issues };
        }

        if (string.IsNullOrWhiteSpace(workDir))
        {
            issues.Add(CreateIssue(
                "workdir.required",
                "Task.Validation.WorkDirRequiredTitle",
                "Task.Validation.WorkDirRequiredDetail",
                TaskValidationSeverity.Error,
                isBlocking: true));
        }
        else
        {
            var workDirExists = await SafeDirectoryExistsAsync(workDir, ct);
            if (!workDirExists)
            {
                issues.Add(CreateIssue(
                    "workdir.missing",
                    "Task.Validation.WorkDirMissingTitle",
                    string.Format(L("Task.Validation.WorkDirMissingDetail"), workDir),
                    TaskValidationSeverity.Error,
                    isBlocking: true,
                    quickFixKind: TaskValidationQuickFixKind.CreateWorkDirectory,
                    quickFixTextKey: "Task.Validation.FixCreateWorkDir",
                    detailIsResourceKey: false));
            }
            else
            {
                var writable = await IsWorkDirectoryWritableAsync(workDir, ct);
                if (!writable)
                {
                    issues.Add(CreateIssue(
                        "workdir.notWritable",
                        "Task.Validation.WorkDirNotWritableTitle",
                        string.Format(L("Task.Validation.WorkDirNotWritableDetail"), workDir),
                        TaskValidationSeverity.Error,
                        isBlocking: true,
                        detailIsResourceKey: false));
                }
            }
        }

        if (!hasCommandPayload && string.IsNullOrWhiteSpace(appPath))
        {
            issues.Add(CreateIssue(
                "app.required",
                "Task.Validation.ProgramRequiredTitle",
                "Task.Validation.ProgramRequiredDetail",
                TaskValidationSeverity.Error,
                isBlocking: true));
        }
        else if (!hasCommandPayload && appPath.StartsWith("/", StringComparison.Ordinal))
        {
            var appExists = await SafeFileExistsAsync(appPath, ct);
            if (!appExists)
            {
                issues.Add(CreateIssue(
                    "app.missing",
                    "Task.Validation.ProgramMissingTitle",
                    string.Format(L("Task.Validation.ProgramMissingDetail"), appPath),
                    TaskValidationSeverity.Error,
                    isBlocking: true,
                    detailIsResourceKey: false));
            }
        }

        if (string.IsNullOrWhiteSpace(sbatchOptions.Partition))
        {
            issues.Add(CreateIssue(
                "sbatch.partition.required",
                "Task.Validation.QueueRequiredTitle",
                "Task.Validation.QueueRequiredDetail",
                TaskValidationSeverity.Error,
                isBlocking: true));
        }

        if (!IsPositiveInteger(sbatchOptions.Nodes))
        {
            issues.Add(CreateIssue(
                "sbatch.nodes.invalid",
                "Task.Validation.NodesInvalidTitle",
                string.Format(L("Task.Validation.NodesInvalidDetail"), sbatchOptions.Nodes ?? string.Empty),
                TaskValidationSeverity.Error,
                isBlocking: true,
                detailIsResourceKey: false));
        }

        if (!IsPositiveInteger(sbatchOptions.TaskCount))
        {
            issues.Add(CreateIssue(
                "sbatch.taskCount.invalid",
                "Task.Validation.TaskCountInvalidTitle",
                string.Format(L("Task.Validation.TaskCountInvalidDetail"), sbatchOptions.TaskCount ?? string.Empty),
                TaskValidationSeverity.Error,
                isBlocking: true,
                detailIsResourceKey: false));
        }

        if (!string.IsNullOrWhiteSpace(sbatchOptions.CpuCount) && !IsPositiveInteger(sbatchOptions.CpuCount))
        {
            issues.Add(CreateIssue(
                "sbatch.cpu.invalid",
                "Task.Validation.CpuInvalidTitle",
                string.Format(L("Task.Validation.CpuInvalidDetail"), sbatchOptions.CpuCount ?? string.Empty),
                TaskValidationSeverity.Error,
                isBlocking: true,
                detailIsResourceKey: false));
        }

        if (string.IsNullOrWhiteSpace(sbatchOptions.Account))
        {
            issues.Add(CreateIssue(
                "sbatch.account.required",
                "Task.Validation.AccountRequiredTitle",
                "Task.Validation.AccountRequiredDetail",
                TaskValidationSeverity.Error,
                isBlocking: true,
                quickFixKind: string.IsNullOrWhiteSpace(context.DefaultAccount)
                    ? TaskValidationQuickFixKind.None
                    : TaskValidationQuickFixKind.FillDefaultAccount,
                quickFixTextKey: "Task.Validation.FixUseDefaultAccount"));
        }

        if (string.IsNullOrWhiteSpace(sbatchOptions.JobName))
        {
            issues.Add(CreateIssue(
                "sbatch.jobName.required",
                "Task.Validation.JobNameRequiredTitle",
                "Task.Validation.JobNameRequiredDetail",
                TaskValidationSeverity.Error,
                isBlocking: true));
        }

        var sbatchTemplate = context.SbatchTemplate ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sbatchTemplate))
        {
            issues.Add(CreateIssue(
                "sbatch.template.required",
                "Task.Validation.SbatchTemplateRequiredTitle",
                "Task.Validation.SbatchTemplateRequiredDetail",
                TaskValidationSeverity.Error,
                isBlocking: true));
        }
        else if (!HasShebang(sbatchTemplate))
        {
            issues.Add(CreateIssue(
                "sbatch.template.shebang",
                "Task.Validation.SbatchShebangTitle",
                "Task.Validation.SbatchShebangDetail",
                TaskValidationSeverity.Error,
                isBlocking: true));
        }

        if (!IsSbatchOptionSynchronized(sbatchTemplate, "--job-name", sbatchOptions.JobName)
            || !IsSbatchOptionSynchronized(sbatchTemplate, "--partition", sbatchOptions.Partition)
            || !IsSbatchOptionSynchronized(sbatchTemplate, "--nodes", sbatchOptions.Nodes)
            || !IsSbatchOptionSynchronized(sbatchTemplate, "--ntasks", sbatchOptions.TaskCount)
            || !IsSbatchOptionSynchronized(sbatchTemplate, "--account", sbatchOptions.Account))
        {
            issues.Add(CreateIssue(
                "sbatch.mismatch",
                "Task.Validation.SbatchMismatchTitle",
                "Task.Validation.SbatchMismatchDetail",
                TaskValidationSeverity.Warning,
                isBlocking: false));
        }

        await ValidateCommandsAsync(commands, issues, ct);

        if (parameterFiles.Count == 0 && !commands.Any(command => !string.IsNullOrWhiteSpace(command.ProgramPath)))
        {
            issues.Add(CreateIssue(
                "param.required",
                "Task.Validation.ParameterRequiredTitle",
                "Task.Validation.ParameterRequiredDetail",
                TaskValidationSeverity.Error,
                isBlocking: true));
        }

        if (!string.IsNullOrWhiteSpace(workDir))
        {
            var missingCopies = new List<string>();
            foreach (var paramPath in parameterFiles)
            {
                var fileName = GetFileNameFromPath(paramPath);
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    issues.Add(CreateIssue(
                        "param.invalidPath",
                        "Task.Validation.ParameterInvalidTitle",
                        string.Format(L("Task.Validation.ParameterInvalidDetail"), paramPath),
                        TaskValidationSeverity.Error,
                        isBlocking: true,
                        detailIsResourceKey: false));
                    continue;
                }

                if (paramPath.StartsWith("/", StringComparison.Ordinal))
                {
                    var sourceExists = await SafeFileExistsAsync(paramPath, ct);
                    if (!sourceExists)
                    {
                        issues.Add(CreateIssue(
                            "param.sourceMissing",
                            "Task.Validation.ParameterSourceMissingTitle",
                            string.Format(L("Task.Validation.ParameterSourceMissingDetail"), paramPath),
                            TaskValidationSeverity.Error,
                            isBlocking: true,
                            detailIsResourceKey: false));
                        continue;
                    }
                }

                var expectedTarget = $"{workDir.TrimEnd('/')}/{fileName}";
                var targetExists = await SafeFileExistsAsync(expectedTarget, ct);
                if (!targetExists)
                    missingCopies.Add(expectedTarget);
            }

            if (missingCopies.Count > 0)
            {
                issues.Add(CreateIssue(
                    "param.copyMissing",
                    "Task.Validation.ParameterCopyMissingTitle",
                    string.Format(L("Task.Validation.ParameterCopyMissingDetail"), missingCopies.Count),
                    TaskValidationSeverity.Error,
                    isBlocking: true,
                    quickFixKind: TaskValidationQuickFixKind.MaterializeParameterCopies,
                    quickFixTextKey: "Task.Validation.FixMaterializeParams",
                    detailIsResourceKey: false));
            }
        }

        var queues = await TryLoadQueuesAsync(ct);
        if (queues == null || queues.Count == 0)
        {
            issues.Add(CreateIssue(
                "queue.metadata.missing",
                "Task.Validation.QueueMetadataMissingTitle",
                "Task.Validation.QueueMetadataMissingDetail",
                TaskValidationSeverity.Warning,
                isBlocking: false,
                quickFixKind: TaskValidationQuickFixKind.RefreshQueueMetadata,
                quickFixTextKey: "Task.Validation.FixRefreshQueues"));
        }
        else if (!string.IsNullOrWhiteSpace(sbatchOptions.Partition)
                 && !queues.Contains(sbatchOptions.Partition.Trim(), StringComparer.Ordinal))
        {
            issues.Add(CreateIssue(
                "queue.unknown",
                "Task.Validation.QueueUnknownTitle",
                string.Format(L("Task.Validation.QueueUnknownDetail"), sbatchOptions.Partition.Trim()),
                TaskValidationSeverity.Warning,
                isBlocking: false,
                quickFixKind: TaskValidationQuickFixKind.RefreshQueueMetadata,
                quickFixTextKey: "Task.Validation.FixRefreshQueues",
                detailIsResourceKey: false));
        }

        return new TaskValidationResult
        {
            Issues = issues
                .OrderByDescending(static i => i.IsBlocking)
                .ThenBy(static i => i.Severity)
                .ToList(),
        };
    }

    private async Task ValidateCommandsAsync(
        IReadOnlyList<CommandValidationItem> commands,
        List<TaskValidationIssue> issues,
        CancellationToken ct)
    {
        foreach (var command in commands.Where(HasCommandPayload))
        {
            var order = command.Order.ToString();
            var hasProgram = !string.IsNullOrWhiteSpace(command.ProgramPath);
            var hasStructuredFields = !string.IsNullOrWhiteSpace(command.MpirunPath) || command.ParameterFiles.Count > 0;

            if (!hasProgram)
            {
                if (hasStructuredFields)
                {
                    issues.Add(CreateIssue(
                        "command.program.required",
                        "Task.Validation.CommandProgramRequiredTitle",
                        string.Format(L("Task.Validation.CommandProgramRequiredDetail"), order),
                        TaskValidationSeverity.Error,
                        isBlocking: true,
                        detailIsResourceKey: false));
                }

                continue;
            }

            var programAvailable = await IsExecutableReferenceAvailableAsync(command.ProgramPath, ct);
            if (!programAvailable)
            {
                issues.Add(CreateIssue(
                    "command.program.unavailable",
                    "Task.Validation.CommandProgramUnavailableTitle",
                    string.Format(L("Task.Validation.CommandProgramUnavailableDetail"), order, command.ProgramPath),
                    TaskValidationSeverity.Error,
                    isBlocking: true,
                    detailIsResourceKey: false));
            }

            if (!string.IsNullOrWhiteSpace(command.MpirunPath))
            {
                var mpiAvailable = await IsExecutableReferenceAvailableAsync(command.MpirunPath, ct);
                if (!mpiAvailable)
                {
                    issues.Add(CreateIssue(
                        "command.mpi.unavailable",
                        "Task.Validation.CommandMpiUnavailableTitle",
                        string.Format(L("Task.Validation.CommandMpiUnavailableDetail"), order, command.MpirunPath),
                        TaskValidationSeverity.Error,
                        isBlocking: true,
                        detailIsResourceKey: false));
                }
            }
            else if (CommandLineUsesMpi(command.CommandLine))
            {
                issues.Add(CreateIssue(
                    "command.mpi.unverified",
                    "Task.Validation.CommandMpiUnverifiedTitle",
                    string.Format(L("Task.Validation.CommandMpiUnverifiedDetail"), order),
                    TaskValidationSeverity.Warning,
                    isBlocking: false,
                    detailIsResourceKey: false));
            }

            if (command.ParameterFiles.Count == 0)
            {
                issues.Add(CreateIssue(
                    "command.parameter.required",
                    "Task.Validation.CommandParameterRequiredTitle",
                    string.Format(L("Task.Validation.CommandParameterRequiredDetail"), order),
                    TaskValidationSeverity.Error,
                    isBlocking: true,
                    detailIsResourceKey: false));
            }
        }
    }

    private async Task<bool> SafeDirectoryExistsAsync(string path, CancellationToken ct)
    {
        try
        {
            return await _ssh.RemoteDirectoryExistsAsync(path, ct);
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> SafeFileExistsAsync(string path, CancellationToken ct)
    {
        try
        {
            return await _ssh.RemoteFileExistsAsync(path, ct);
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> IsWorkDirectoryWritableAsync(string workDir, CancellationToken ct)
    {
        try
        {
            var escaped = EscapeShellArg(workDir);
            var (_, _, exitCode) = await _ssh.ExecuteAsync($"test -w {escaped}", ct);
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> IsExecutableReferenceAvailableAsync(string value, CancellationToken ct)
    {
        try
        {
            var normalized = NormalizeRemotePath(value);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            var escaped = EscapeShellArg(normalized);
            var command = normalized.StartsWith("/", StringComparison.Ordinal) || normalized.Contains("/", StringComparison.Ordinal)
                ? $"test -f {escaped} && test -x {escaped}"
                : $"command -v {escaped} >/dev/null 2>&1";
            var (_, _, exitCode) = await _ssh.ExecuteAsync(command, ct);
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<string>?> TryLoadQueuesAsync(CancellationToken ct)
    {
        try
        {
            var (stdout, _, exitCode) = await _ssh.ExecuteAsync("sinfo --noheader --format=\"%P\"", ct);
            if (exitCode != 0)
                return null;
            return stdout
                .Replace("\r\n", "\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim().TrimEnd('*'))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch
        {
            return null;
        }
    }

    private static bool HasShebang(string script)
    {
        var firstNonEmptyLine = script
            .Replace("\r\n", "\n")
            .Split('\n')
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        return firstNonEmptyLine != null && firstNonEmptyLine.TrimStart().StartsWith("#!", StringComparison.Ordinal);
    }

    private static bool IsSbatchOptionSynchronized(string script, string directiveName, string? optionValue)
    {
        if (!TryReadDirective(script, directiveName, out var scriptValue))
            return true;

        return string.Equals(
            (scriptValue ?? string.Empty).Trim(),
            (optionValue ?? string.Empty).Trim(),
            StringComparison.Ordinal);
    }

    private static bool TryReadDirective(string script, string directiveName, out string value)
    {
        foreach (var line in script.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            var prefix = $"#SBATCH {directiveName}";
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var remaining = trimmed[prefix.Length..].TrimStart();
            if (remaining.StartsWith("=", StringComparison.Ordinal))
                remaining = remaining[1..].Trim();
            value = remaining;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool IsPositiveInteger(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (!PositiveIntegerRegex.IsMatch(text))
            return false;
        return int.TryParse(text, out var parsed) && parsed > 0;
    }

    private static string GetFileNameFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        var normalized = path.Trim().Replace('\\', '/');
        var slashIndex = normalized.LastIndexOf('/');
        return slashIndex >= 0 ? normalized[(slashIndex + 1)..] : normalized;
    }

    private static string NormalizeRemotePath(string? path)
    {
        var normalized = (path ?? string.Empty).Trim().Replace('\\', '/');
        if (normalized == "/")
            return "/";
        return normalized.TrimEnd('/');
    }

    private static bool HasCommandPayload(CommandValidationItem command)
        => !string.IsNullOrWhiteSpace(command.CommandLine)
           || !string.IsNullOrWhiteSpace(command.ProgramPath)
           || !string.IsNullOrWhiteSpace(command.MpirunPath)
           || command.ParameterFiles.Count > 0;

    private static bool CommandLineUsesMpi(string commandLine)
        => !string.IsNullOrWhiteSpace(commandLine) && MpiLauncherRegex.IsMatch(commandLine);

    private static string EscapeShellArg(string value)
        => "'" + value.Replace("'", "'\\''") + "'";

    private static TaskValidationIssue CreateIssue(
        string code,
        string titleKey,
        string detailOrKey,
        TaskValidationSeverity severity,
        bool isBlocking,
        TaskValidationQuickFixKind quickFixKind = TaskValidationQuickFixKind.None,
        string? quickFixTextKey = null,
        bool detailIsResourceKey = true)
    {
        return new TaskValidationIssue
        {
            Code = code,
            Title = L(titleKey),
            Description = detailIsResourceKey ? L(detailOrKey) : detailOrKey,
            Severity = severity,
            IsBlocking = isBlocking,
            QuickFixKind = quickFixKind,
            QuickFixText = string.IsNullOrWhiteSpace(quickFixTextKey) ? string.Empty : L(quickFixTextKey),
        };
    }

    private static string L(string key)
        => Application.Current?.TryFindResource(key) as string ?? key;
}
