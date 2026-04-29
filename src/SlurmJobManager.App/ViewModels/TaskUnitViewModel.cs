using System.Collections.ObjectModel;
using SlurmJobManager.Core.Models;

namespace SlurmJobManager.App.ViewModels;

/// <summary>
/// Observable wrapper around a <see cref="TaskUnit"/> model.
/// Exposes mutable collections for programs, parameter files and commands
/// so the UI can bind and edit them directly.
/// </summary>
public sealed class TaskUnitViewModel : ViewModelBase
{
    private string _taskName;
    private bool   _enabled;

    public TaskUnitViewModel(TaskUnit model)
    {
        _taskName = model.TaskName;
        _enabled  = model.Enabled;

        foreach (var p in model.ProgramEntries)
            Programs.Add(new ProgramEntryViewModel(p));

        foreach (var f in model.ParameterFiles)
            ParamFiles.Add(new ParameterFileEntryViewModel(f));

        foreach (var c in model.CommandEntries)
            Commands.Add(new CommandEntryViewModel(c));

        foreach (var (k, v) in model.ExtraParameters)
            ExtraParams.Add(new ParameterEntry { Key = k, Value = v });

        RemoteWorkDirectory = model.RemoteWorkDirectory;
        SlurmJobId          = model.SlurmJobId;
        SbatchTemplate      = model.SbatchTemplate;
    }

    // ── Scalar properties ────────────────────────────────────────────────────

    public string TaskName
    {
        get => _taskName;
        set => SetField(ref _taskName, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    public string? RemoteWorkDirectory { get; set; }
    public long?   SlurmJobId          { get; set; }
    public string? SbatchTemplate      { get; set; }

    // ── Collections ──────────────────────────────────────────────────────────

    public ObservableCollection<ProgramEntryViewModel>       Programs    { get; } = new();
    public ObservableCollection<ParameterFileEntryViewModel> ParamFiles  { get; } = new();
    public ObservableCollection<CommandEntryViewModel>       Commands    { get; } = new();
    public ObservableCollection<ParameterEntry>              ExtraParams { get; } = new();

    // ── Snapshot back to model ────────────────────────────────────────────────

    public TaskUnit ToModel() => new()
    {
        TaskName            = TaskName,
        Enabled             = Enabled,
        RemoteWorkDirectory = RemoteWorkDirectory,
        SlurmJobId          = SlurmJobId,
        SbatchTemplate      = SbatchTemplate,
        ProgramEntries      = Programs.Select(p => p.ToModel()).ToList(),
        ParameterFiles      = ParamFiles.Select(f => f.ToModel()).ToList(),
        CommandEntries      = Commands.Select(c => c.ToModel()).ToList(),
        ExtraParameters     = ExtraParams
            .Where(e => !string.IsNullOrWhiteSpace(e.Key))
            .ToDictionary(e => e.Key, e => e.Value),
    };
}

// ── Thin view-model wrappers for child entries ────────────────────────────────

public sealed class ProgramEntryViewModel : ViewModelBase
{
    private string  _programPath;
    private string? _argsTemplate;
    private int     _order;

    public ProgramEntryViewModel(ProgramEntry m)
    {
        _programPath  = m.ProgramPath;
        _argsTemplate = m.ArgsTemplate;
        _order        = m.Order;
    }

    public ProgramEntryViewModel() { _programPath = string.Empty; }

    public string  ProgramPath  { get => _programPath;  set => SetField(ref _programPath,  value); }
    public string? ArgsTemplate { get => _argsTemplate; set => SetField(ref _argsTemplate, value); }
    public int     Order        { get => _order;        set => SetField(ref _order,        value); }

    public ProgramEntry ToModel() => new()
    {
        ProgramPath  = ProgramPath,
        ArgsTemplate = ArgsTemplate,
        Order        = Order,
    };
}

public sealed class ParameterFileEntryViewModel : ViewModelBase
{
    private string  _filePath;
    private string? _alias;
    private bool    _isPinned;

    public ParameterFileEntryViewModel(ParameterFileEntry m)
    {
        _filePath  = m.FilePath;
        _alias     = m.Alias;
        _isPinned  = m.IsPinned;
    }

    public ParameterFileEntryViewModel() { _filePath = string.Empty; }

    public string  FilePath  { get => _filePath;  set => SetField(ref _filePath,  value); }
    public string? Alias     { get => _alias;     set => SetField(ref _alias,     value); }
    public bool    IsPinned  { get => _isPinned;  set => SetField(ref _isPinned,  value); }

    public ParameterFileEntry ToModel() => new()
    {
        FilePath = FilePath,
        Alias    = Alias,
        IsPinned = IsPinned,
    };
}

public sealed class CommandEntryViewModel : ViewModelBase
{
    private string  _commandLine;
    private string? _description;
    private int     _order;

    public CommandEntryViewModel(CommandEntry m)
    {
        _commandLine = m.CommandLine;
        _description = m.Description;
        _order       = m.Order;
    }

    public CommandEntryViewModel() { _commandLine = string.Empty; }

    public string  CommandLine  { get => _commandLine;  set => SetField(ref _commandLine,  value); }
    public string? Description  { get => _description;  set => SetField(ref _description,  value); }
    public int     Order        { get => _order;        set => SetField(ref _order,        value); }

    public CommandEntry ToModel() => new()
    {
        CommandLine = CommandLine,
        Description = Description,
        Order       = Order,
    };
}
