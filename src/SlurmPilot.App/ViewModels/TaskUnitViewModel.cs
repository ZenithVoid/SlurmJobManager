using System.Collections.ObjectModel;
using SlurmPilot.Core.Models;

namespace SlurmPilot.App.ViewModels;

/// <summary>
/// Observable wrapper around a <see cref="TaskUnit"/> model.
/// Exposes mutable collections for programs, parameter files and commands
/// so the UI can bind and edit them directly.
/// </summary>
public sealed class TaskUnitViewModel : ViewModelBase
{
    private string _taskName;
    private bool   _enabled;
    private SbatchJobOptions _sbatchOptions;

    public TaskUnitViewModel(TaskUnit model)
    {
        _taskName = model.TaskName;
        _enabled  = model.Enabled;
        _sbatchOptions = model.SbatchOptions ?? new SbatchJobOptions();

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
        SourceBlueprintId   = model.SourceBlueprintId;
        SourceBlueprintName = model.SourceBlueprintName;
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
    public string? SourceBlueprintId   { get; set; }
    public string? SourceBlueprintName { get; set; }
    public string? SbatchTemplate      { get; set; }
    public SbatchJobOptions SbatchOptions
    {
        get => _sbatchOptions;
        set => SetField(ref _sbatchOptions, value);
    }

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
        SourceBlueprintId   = SourceBlueprintId,
        SourceBlueprintName = SourceBlueprintName,
        SbatchTemplate      = SbatchTemplate,
        SbatchOptions       = CloneSbatchOptions(SbatchOptions),
        ProgramEntries      = Programs.Select(p => p.ToModel()).ToList(),
        ParameterFiles      = ParamFiles.Select(f => f.ToModel()).ToList(),
        CommandEntries      = Commands.Select(c => c.ToModel()).ToList(),
        ExtraParameters     = ExtraParams
            .Where(e => !string.IsNullOrWhiteSpace(e.Key))
            .ToDictionary(e => e.Key, e => e.Value),
    };

    private static SbatchJobOptions CloneSbatchOptions(SbatchJobOptions? source) => new()
    {
        JobName = source?.JobName ?? string.Empty,
        Partition = source?.Partition ?? string.Empty,
        Nodes = source?.Nodes ?? "1",
        TaskCount = source?.TaskCount ?? "1",
        CpuCount = source?.CpuCount ?? string.Empty,
        GpuCount = source?.GpuCount ?? string.Empty,
        TimeLimit = source?.TimeLimit ?? string.Empty,
        Account = source?.Account ?? "preproc",
        Exclusive = source?.Exclusive ?? false,
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
    // Required MPI launch options for this project; IFACE_NAME is prepared in the sbatch header.
    private const string MpiLaunchArgsWithoutBinding = "-np $SLURM_NPROCS --mca btl_tcp_if_include $IFACE_NAME";

    private string  _commandLine;
    private string? _description;
    private int     _order;
    private string  _programPath;
    private string? _mpirunPath;
    private string? _pythonInterpreterPath;
    private bool _usePythonInterpreter;
    private bool _includeBindToNone = true;

    public CommandEntryViewModel(CommandEntry m)
    {
        _commandLine = m.CommandLine;
        _description = m.Description;
        _order       = m.Order;
        _programPath = m.ProgramPath;
        _mpirunPath  = m.MpirunPath;
        _pythonInterpreterPath = m.PythonInterpreterPath;
        _usePythonInterpreter = m.UsePythonInterpreter || !string.IsNullOrWhiteSpace(m.PythonInterpreterPath);

        foreach (var pf in m.ParameterFiles)
            ParameterFiles.Add(pf);

        foreach (var ea in m.ExtraArgs)
            ExtraArgs.Add(new ExtraArgViewModel(ea));

        foreach (var ev in m.EnvironmentVariables)
            EnvironmentVariables.Add(new EnvironmentVariableViewModel(ev));
    }

    public CommandEntryViewModel() { _commandLine = string.Empty; _programPath = string.Empty; }

    public string  CommandLine  { get => _commandLine;  set { if (SetField(ref _commandLine, value)) OnPropertyChanged(nameof(DisplaySummary)); } }
    public string? Description  { get => _description;  set => SetField(ref _description,  value); }
    public int     Order        { get => _order;        set => SetField(ref _order,        value); }

    public string ProgramPath
    {
        get => _programPath;
        set { if (SetField(ref _programPath, value)) { RebuildCommandLine(); OnPropertyChanged(nameof(DisplaySummary)); } }
    }

    public string? MpirunPath
    {
        get => _mpirunPath;
        set { if (SetField(ref _mpirunPath, value)) RebuildCommandLine(); }
    }

    public string? PythonInterpreterPath
    {
        get => _pythonInterpreterPath;
        set
        {
            if (SetField(ref _pythonInterpreterPath, value))
            {
                if (!string.IsNullOrWhiteSpace(value))
                    UsePythonInterpreter = true;
                RebuildCommandLine();
                OnPropertyChanged(nameof(DisplaySummary));
            }
        }
    }

    public bool UsePythonInterpreter
    {
        get => _usePythonInterpreter;
        set
        {
            if (SetField(ref _usePythonInterpreter, value))
            {
                if (!value)
                    SetField(ref _pythonInterpreterPath, null, nameof(PythonInterpreterPath));
                RebuildCommandLine();
                OnPropertyChanged(nameof(DisplaySummary));
            }
        }
    }

    public bool IncludeBindToNone
    {
        get => _includeBindToNone;
        set { if (SetField(ref _includeBindToNone, value)) RebuildCommandLine(); }
    }

    public ObservableCollection<string>         ParameterFiles { get; } = new();
    public ObservableCollection<ExtraArgViewModel> ExtraArgs   { get; } = new();
    public ObservableCollection<EnvironmentVariableViewModel> EnvironmentVariables { get; } = new();

    /// <summary>Short label shown in the command list (program file name or raw command line).</summary>
    public string DisplaySummary
    {
        get
        {
            if (_usePythonInterpreter)
            {
                var label = string.IsNullOrWhiteSpace(_pythonInterpreterPath)
                    ? "Python"
                    : $"Python · {GetFileNameFromPath(_pythonInterpreterPath)}";
                return EnvironmentVariables.Count == 0 ? label : $"{label} · env {EnvironmentVariables.Count}";
            }

            if (!string.IsNullOrWhiteSpace(_programPath))
            {
                var name = GetFileNameFromPath(_programPath);
                var label = string.IsNullOrWhiteSpace(name) ? _programPath : name;
                label = $"程序 · {label}";
                return EnvironmentVariables.Count == 0 ? label : $"{label} · env {EnvironmentVariables.Count}";
            }
            return string.IsNullOrWhiteSpace(_commandLine) ? "(empty)" : _commandLine;
        }
    }

    /// <summary>Rebuild <see cref="CommandLine"/> from rich structured fields.</summary>
    public void RebuildCommandLine()
    {
        if (!_usePythonInterpreter && string.IsNullOrWhiteSpace(_programPath))
        {
            CommandLine = string.Empty;
            return;
        }

        if (_usePythonInterpreter && string.IsNullOrWhiteSpace(_pythonInterpreterPath))
        {
            CommandLine = string.Empty;
            return;
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(_mpirunPath))
        {
            parts.Add(_mpirunPath);
            if (_includeBindToNone)
                parts.Add("--bind-to none");
            parts.Add(MpiLaunchArgsWithoutBinding);
        }

        foreach (var env in EnvironmentVariables.Where(e => !string.IsNullOrWhiteSpace(e.Key)))
            parts.Add($"{env.Key.Trim()}={EscapeShellValue(env.Value)}");

        if (_usePythonInterpreter && !string.IsNullOrWhiteSpace(_pythonInterpreterPath))
            parts.Add(_pythonInterpreterPath);

        if (!_usePythonInterpreter && !string.IsNullOrWhiteSpace(_programPath))
            parts.Add(_programPath);
        foreach (var pf in ParameterFiles.Where(p => !string.IsNullOrWhiteSpace(p)))
            parts.Add(ToWorkDirRelativeParamArg(pf));
        foreach (var ea in ExtraArgs.Where(a => !string.IsNullOrWhiteSpace(a.Arg)))
            parts.Add(ea.Arg);

        CommandLine = string.Join(" ", parts);
    }

    private static string ToWorkDirRelativeParamArg(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized))
            return normalized;

        var fileName = normalized;
        var slashIndex = normalized.LastIndexOf('/');
        if (slashIndex >= 0 && slashIndex + 1 < normalized.Length)
            fileName = normalized[(slashIndex + 1)..];

        if (fileName.StartsWith("./", StringComparison.Ordinal))
            return fileName;

        return $"./{fileName.TrimStart('/')}";
    }

    private static string GetFileNameFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var normalized = path.Trim().Replace('\\', '/');
        var slashIdx = normalized.LastIndexOf('/');
        return slashIdx >= 0 ? normalized[(slashIdx + 1)..] : normalized;
    }

    private static string EscapeShellValue(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length == 0)
            return "''";
        if (text.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' or '/' or ':'))
            return text;
        return "'" + text.Replace("'", "'\\''") + "'";
    }

    public CommandEntry ToModel() => new()
    {
        CommandLine    = CommandLine,
        Description    = Description,
        Order          = Order,
        ProgramPath    = ProgramPath,
        ParameterFiles = ParameterFiles.ToList(),
        ExtraArgs      = ExtraArgs.Select(a => a.Arg).ToList(),
        EnvironmentVariables = EnvironmentVariables.Select(v => v.ToModel()).ToList(),
        PythonInterpreterPath = UsePythonInterpreter ? PythonInterpreterPath : null,
        UsePythonInterpreter = UsePythonInterpreter,
        MpirunPath     = MpirunPath,
    };
}

/// <summary>A single extra command-line argument entry within a command.</summary>
public sealed class ExtraArgViewModel : ViewModelBase
{
    private string _arg;

    public ExtraArgViewModel(string arg = "") { _arg = arg; }

    public string Arg
    {
        get => _arg;
        set => SetField(ref _arg, value);
    }
}

public sealed class EnvironmentVariableViewModel : ViewModelBase
{
    private string _key;
    private string _value;

    public EnvironmentVariableViewModel(EnvironmentVariableEntry? entry = null)
    {
        _key = entry?.Key ?? string.Empty;
        _value = entry?.Value ?? string.Empty;
    }

    public string Key
    {
        get => _key;
        set => SetField(ref _key, value);
    }

    public string Value
    {
        get => _value;
        set => SetField(ref _value, value);
    }

    public EnvironmentVariableEntry ToModel() => new()
    {
        Key = Key,
        Value = Value,
    };
}
