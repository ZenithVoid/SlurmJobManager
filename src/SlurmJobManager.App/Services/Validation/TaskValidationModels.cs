using SlurmJobManager.Core.Models;

namespace SlurmJobManager.App.Services.Validation;

public enum TaskValidationOperation
{
    Save,
    Submit,
}

public enum TaskValidationSeverity
{
    Error,
    Warning,
    Info,
}

public enum TaskValidationQuickFixKind
{
    None,
    CreateWorkDirectory,
    MaterializeParameterCopies,
    RefreshQueueMetadata,
    FillDefaultAccount,
}

public sealed class TaskValidationIssue
{
    public string Code { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public TaskValidationSeverity Severity { get; init; } = TaskValidationSeverity.Info;
    public bool IsBlocking { get; init; }
    public TaskValidationQuickFixKind QuickFixKind { get; init; } = TaskValidationQuickFixKind.None;
    public string QuickFixText { get; init; } = string.Empty;
}

public sealed class TaskValidationResult
{
    public IReadOnlyList<TaskValidationIssue> Issues { get; init; } = Array.Empty<TaskValidationIssue>();
    public bool HasBlockingIssues => Issues.Any(static i => i.IsBlocking);
}

public sealed class CommandValidationItem
{
    public int Order { get; init; }
    public string CommandLine { get; init; } = string.Empty;
    public string ProgramPath { get; init; } = string.Empty;
    public string MpirunPath { get; init; } = string.Empty;
    public IReadOnlyList<string> ParameterFiles { get; init; } = Array.Empty<string>();
}

public sealed class TaskValidationContext
{
    public bool IsSshConnected { get; init; }
    public string RootDirectory { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public string EffectiveWorkDirectory { get; init; } = string.Empty;
    public string AppPath { get; init; } = string.Empty;
    public string SbatchTemplate { get; init; } = string.Empty;
    public SbatchJobOptions SbatchOptions { get; init; } = new();
    public IReadOnlyList<string> ParameterFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<CommandValidationItem> Commands { get; init; } = Array.Empty<CommandValidationItem>();
    public string DefaultAccount { get; init; } = string.Empty;
}
