using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using SlurmPilot.App.Services;
using SlurmPilot.App.ViewModels;
using SlurmPilot.App.Views;
using SlurmPilot.App.Views.Dialogs;
using SlurmPilot.Core.Interfaces;
using SlurmPilot.Core.Models;

namespace SlurmPilot.App.ViewModels.Dialogs;

/// <summary>
/// View-model for the upgraded Command Builder dialog.
/// Supports multiple commands per unit, MPI path auto-detection via ldd,
/// per-command extra args, and an editable sbatch script.
/// </summary>
public sealed class CommandBuilderViewModel : ViewModelBase
{
    // ── Remote source directories ───────────────────────────────────────────
    private static readonly string[] AppSourceDirs = { "/env/preprocess/out", "/env/preprocess/bin" };
    private static readonly Regex LeadingIntRegex = new(@"\d+", RegexOptions.Compiled);
    private static readonly Regex GpuGresRegex = new(@"\bgpu(?::|\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GpuCountRegex = new(@"\bgpu(?::[^,:\s]+)?:(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private const string RemoteParamDir = "/env/preprocess/out/config";
    private const string InterfaceProbeIp = "10.10.10.202";
    private const string DefaultNodes = "1";
    private const string DefaultTaskCount = "1";
    private const string DefaultTimeLimit = "";
    private const string DefaultAccount = "preproc";

    private readonly ISshClientService _ssh;
    private readonly AppPreferencesService _prefs;
    private readonly TaskPathLibraryService _pathLibrary = TaskPathLibraryService.Instance;
    // ── Context passed from TaskEditorViewModel ────────────────────────────
    private readonly string _taskId;
    private readonly string _remoteWorkDir;

    // ── Scalar backing fields ──────────────────────────────────────────────
    private string  _programFilter   = string.Empty;
    private string  _paramFilter     = string.Empty;
    private string  _sbatchContent   = string.Empty;
    private string  _statusMessage   = string.Empty;
    private string  _selectedAvailableProgram = string.Empty;
    private bool    _isBusy;
    private CommandEntryViewModel? _selectedCommand;
    private string  _selectedAvailableParamFile = string.Empty;
    private string _homeDirectory = string.Empty;
    private string _sbatchJobName = string.Empty;
    private string _sbatchPartition = string.Empty;
    private string _sbatchNodes = DefaultNodes;
    private string _sbatchTaskCount = DefaultTaskCount;
    private string _sbatchCpuCount = string.Empty;
    private string _sbatchGpuCount = string.Empty;
    private string _sbatchTimeLimit = DefaultTimeLimit;
    private int _sbatchTimeYears;
    private int _sbatchTimeMonths;
    private int _sbatchTimeDays;
    private bool _isSyncingTimeSelection;
    private string _sbatchAccount = DefaultAccount;
    private bool _sbatchExclusive;
    private readonly Dictionary<string, QueueMetadata> _queueMetadataMap = new(StringComparer.Ordinal);

    // ── Result ─────────────────────────────────────────────────────────────
    public bool Confirmed { get; private set; }

    // ── Available remote file lists ────────────────────────────────────────
    public ObservableCollection<string> AllPrograms      { get; } = new();
    public ObservableCollection<string> FilteredPrograms { get; } = new();
    public ObservableCollection<string> AllParamFiles    { get; } = new();
    public ObservableCollection<string> FilteredParamFiles { get; } = new();
    public ObservableCollection<PythonInterpreterOption> PythonInterpreters { get; } = new();
    public ObservableCollection<string> AvailableQueues { get; } = new();
    public ObservableCollection<GpuCountOption> SbatchGpuCountOptions { get; } = new();
    public IReadOnlyList<int> SbatchTimeYearOptions { get; } = Enumerable.Range(0, 11).ToList();
    public IReadOnlyList<int> SbatchTimeMonthOptions { get; } = Enumerable.Range(0, 13).ToList();
    public IReadOnlyList<int> SbatchTimeDayOptions { get; } = Enumerable.Range(0, 32).ToList();

    // ── Commands list ──────────────────────────────────────────────────────
    /// <summary>Ordered list of commands in the current task unit.</summary>
    public ObservableCollection<CommandEntryViewModel> Commands { get; } = new();

    /// <summary>Currently selected command for detail editing.</summary>
    public CommandEntryViewModel? SelectedCommand
    {
        get => _selectedCommand;
        set
        {
            if (SetField(ref _selectedCommand, value))
            {
                OnPropertyChanged(nameof(HasSelectedCommand));
                OnPropertyChanged(nameof(SelectedCommandValidationSummary));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool HasSelectedCommand => _selectedCommand != null;
    public bool HasCommands => Commands.Count > 0;
    public string SelectedCommandValidationSummary => BuildSelectedCommandValidationSummary();

    // ── Filters ────────────────────────────────────────────────────────────
    public string ProgramFilter
    {
        get => _programFilter;
        set { if (SetField(ref _programFilter, value)) RebuildFilteredPrograms(); }
    }

    public string ParamFilter
    {
        get => _paramFilter;
        set { if (SetField(ref _paramFilter, value)) RebuildFilteredParamFiles(); }
    }

    public string SelectedAvailableProgram
    {
        get => _selectedAvailableProgram;
        set
        {
            if (SetField(ref _selectedAvailableProgram, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public string SelectedAvailableParamFile
    {
        get => _selectedAvailableParamFile;
        set { if (SetField(ref _selectedAvailableParamFile, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    // ── sbatch content ─────────────────────────────────────────────────────
    /// <summary>Editable sbatch script for this unit.</summary>
    public string SbatchContent
    {
        get => _sbatchContent;
        set => SetField(ref _sbatchContent, value);
    }

    // ── Status ─────────────────────────────────────────────────────────────
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { SetField(ref _isBusy, value); CommandManager.InvalidateRequerySuggested(); }
    }

    public string SbatchJobName
    {
        get => _sbatchJobName;
        set
        {
            if (SetField(ref _sbatchJobName, value))
                RegenerateSbatch();
        }
    }

    public string SbatchPartition
    {
        get => _sbatchPartition;
        set
        {
            if (SetField(ref _sbatchPartition, value))
            {
                OnPropertyChanged(nameof(HasSelectedQueueMetadata));
                OnPropertyChanged(nameof(SelectedQueueMetadataSummary));
                OnPropertyChanged(nameof(SelectedQueueSupportsGpu));
                RebuildGpuCountOptions();
                if (!SelectedQueueSupportsGpu)
                    SbatchGpuCount = string.Empty;
                RegenerateSbatch();
            }
        }
    }

    public string SbatchNodes
    {
        get => _sbatchNodes;
        set
        {
            if (SetField(ref _sbatchNodes, value))
                RegenerateSbatch();
        }
    }

    public string SbatchCpuCount
    {
        get => _sbatchCpuCount;
        set
        {
            if (SetField(ref _sbatchCpuCount, value))
            {
                ApplyMpiBindingPolicy();
                RegenerateSbatch();
            }
        }
    }

    public string SbatchGpuCount
    {
        get => _sbatchGpuCount;
        set
        {
            if (SetField(ref _sbatchGpuCount, NormalizeGpuCount(value)))
                RegenerateSbatch();
        }
    }

    public string SbatchTaskCount
    {
        get => _sbatchTaskCount;
        set
        {
            if (SetField(ref _sbatchTaskCount, value))
                RegenerateSbatch();
        }
    }

    public string SbatchTimeLimit
    {
        get => _sbatchTimeLimit;
        set => SetSbatchTimeLimit(value);
    }

    public int SbatchTimeYears
    {
        get => _sbatchTimeYears;
        set
        {
            if (SetField(ref _sbatchTimeYears, Math.Max(0, value)))
                UpdateTimeLimitFromSelections();
        }
    }

    public int SbatchTimeMonths
    {
        get => _sbatchTimeMonths;
        set
        {
            if (SetField(ref _sbatchTimeMonths, Math.Max(0, value)))
                UpdateTimeLimitFromSelections();
        }
    }

    public int SbatchTimeDays
    {
        get => _sbatchTimeDays;
        set
        {
            if (SetField(ref _sbatchTimeDays, Math.Max(0, value)))
                UpdateTimeLimitFromSelections();
        }
    }

    public string SbatchAccount
    {
        get => _sbatchAccount;
        set
        {
            if (SetField(ref _sbatchAccount, value))
                RegenerateSbatch();
        }
    }

    public bool SbatchExclusive
    {
        get => _sbatchExclusive;
        set
        {
            if (SetField(ref _sbatchExclusive, value))
                RegenerateSbatch();
        }
    }
    public bool HasSelectedQueueMetadata => GetSelectedQueueMetadata() != null;
    public bool SelectedQueueSupportsGpu => GetSelectedQueueMetadata()?.HasGpu == true;
    public string SelectedQueueMetadataSummary => BuildSelectedQueueMetadataSummary();

    // ── Commands ───────────────────────────────────────────────────────────
    public ICommand RefreshCommand          { get; }
    public ICommand AddCommandCommand       { get; }
    public ICommand RemoveCommandCommand    { get; }
    public ICommand MoveUpCommand           { get; }
    public ICommand MoveDownCommand         { get; }
    public ICommand ApplySelectedProgramCommand { get; }
    public ICommand AddParamFileCommand     { get; }
    public ICommand CreateParamFileCommand  { get; }
    public ICommand RemoveParamFileCommand  { get; }
    public ICommand EditParamFileCommand    { get; }
    public ICommand AddExtraArgCommand      { get; }
    public ICommand RemoveExtraArgCommand   { get; }
    public ICommand AddEnvironmentVariableCommand { get; }
    public ICommand RemoveEnvironmentVariableCommand { get; }
    public ICommand DetectMpiCommand        { get; }
    public ICommand TestCommandCommand      { get; }
    public ICommand BrowseProgramPathCommand { get; }
    public ICommand BrowseParamFileCommand   { get; }
    public ICommand BrowsePythonInterpreterCommand { get; }
    public ICommand SaveAndApplyCommand     { get; }
    public ICommand CancelCommand           { get; }

    // ── Constructor ────────────────────────────────────────────────────────

    public CommandBuilderViewModel(
        ISshClientService ssh,
        AppPreferencesService prefs,
        string taskId = "",
        string remoteWorkDir = "",
        IEnumerable<CommandEntry>? initialCommands = null,
        string? initialSbatch = null,
        SbatchJobOptions? initialSbatchOptions = null)
    {
        _ssh           = ssh           ?? throw new ArgumentNullException(nameof(ssh));
        _prefs         = prefs         ?? throw new ArgumentNullException(nameof(prefs));
        _taskId        = taskId;
        _remoteWorkDir = remoteWorkDir;
        Commands.CollectionChanged += (_, __) =>
        {
            OnPropertyChanged(nameof(HasCommands));
            CommandManager.InvalidateRequerySuggested();
        };
        RebuildGpuCountOptions();

        // Populate commands from the task unit
        if (initialCommands != null)
        {
            foreach (var ce in initialCommands)
                Commands.Add(new CommandEntryViewModel(ce));
        }

        SelectedCommand = Commands.FirstOrDefault();

        ApplyInitialSbatchOptions(taskId, initialSbatchOptions, initialSbatch);
        RebuildGpuCountOptions();
        ApplyMpiBindingPolicy();

        // sbatch content
        _sbatchContent = !string.IsNullOrEmpty(initialSbatch)
            ? initialSbatch
            : BuildDefaultSbatch();

        // Wire collection-change notifications for selected command rebuild
        foreach (var cmd in Commands)
            WireCommandCollections(cmd);

        // Commands
        RefreshCommand         = new AsyncRelayCommand(RefreshAsync,         CanRunSshCommand);
        AddCommandCommand      = new AsyncRelayCommand(AddCommandAsync);
        RemoveCommandCommand   = new RelayCommand<CommandEntryViewModel>(RemoveCommand, c => c != null && Commands.Count > 0);
        MoveUpCommand          = new RelayCommand<CommandEntryViewModel>(MoveUp,   c => c != null && Commands.IndexOf(c) > 0);
        MoveDownCommand        = new RelayCommand<CommandEntryViewModel>(MoveDown, c => c != null && Commands.IndexOf(c) < Commands.Count - 1);
        ApplySelectedProgramCommand = new AsyncRelayCommand(ApplySelectedProgramAsync, () => _selectedCommand != null && !string.IsNullOrWhiteSpace(_selectedAvailableProgram));
        AddParamFileCommand    = new AsyncRelayCommand(AddParamFileAsync, () => CanRunSshCommand() && _selectedCommand != null && !string.IsNullOrWhiteSpace(_selectedAvailableParamFile));
        CreateParamFileCommand = new AsyncRelayCommand(CreateParamFileAsync, () => CanRunSshCommand() && _selectedCommand != null);
        RemoveParamFileCommand = new RelayCommand<string>(RemoveParamFile);
        EditParamFileCommand   = new AsyncRelayCommand<string>(EditParamFileAsync);
        AddExtraArgCommand     = new RelayCommand(AddExtraArg,    () => _selectedCommand != null);
        RemoveExtraArgCommand  = new RelayCommand<ExtraArgViewModel>(RemoveExtraArg);
        AddEnvironmentVariableCommand = new RelayCommand(AddEnvironmentVariable, () => _selectedCommand != null);
        RemoveEnvironmentVariableCommand = new RelayCommand<EnvironmentVariableViewModel>(RemoveEnvironmentVariable);
        DetectMpiCommand       = new AsyncRelayCommand(DetectMpiAsync, () => CanRunSshCommand() && _selectedCommand is { UsePythonInterpreter: false } && !string.IsNullOrWhiteSpace(_selectedCommand.ProgramPath));
        TestCommandCommand     = new AsyncRelayCommand(TestCommandAsync, () => CanRunSshCommand() && !string.IsNullOrWhiteSpace(_selectedCommand?.CommandLine));
        BrowseProgramPathCommand = new AsyncRelayCommand(BrowseProgramPathAsync, () => CanRunSshCommand() && _selectedCommand is { UsePythonInterpreter: false });
        BrowseParamFileCommand   = new AsyncRelayCommand(BrowseParamFileAsync, () => CanRunSshCommand() && _selectedCommand != null);
        BrowsePythonInterpreterCommand = new AsyncRelayCommand(BrowsePythonInterpreterAsync, () => CanRunSshCommand() && _selectedCommand != null);
        SaveAndApplyCommand    = new RelayCommand(SaveAndApply);
        CancelCommand          = new RelayCommand(() => { /* handled in code-behind */ });
    }

    // ── Initialization ─────────────────────────────────────────────────────

    public async Task LoadInitialAsync(CancellationToken ct = default)
        => await RefreshAsync(ct);

    // ── Remote refresh ─────────────────────────────────────────────────────

    private async Task RefreshAsync(CancellationToken ct)
    {
        IsBusy = true;
        StatusMessage = "加载远程文件列表…";
        try
        {
            var allPrograms = new List<string>();
            foreach (var dir in AppSourceDirs)
            {
                try
                {
                    var files = await _ssh.ListFilesAsync(dir, ct);
                    allPrograms.AddRange(files.Select(f => $"{dir}/{f}"));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CommandBuilderViewModel] ListFiles({dir}): {ex.Message}");
                }
            }
            AllPrograms.Clear();
            foreach (var p in allPrograms) AllPrograms.Add(p);
            RebuildFilteredPrograms();

            try
            {
                var paramFiles = await _ssh.ListFilesAsync(RemoteParamDir, ct);
                AllParamFiles.Clear();
                foreach (var f in paramFiles) AllParamFiles.Add($"{RemoteParamDir}/{f}");
                RebuildFilteredParamFiles();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CommandBuilderViewModel] ListFiles({RemoteParamDir}): {ex.Message}");
            }

            await LoadQueueMetadataAsync(ct);
            await LoadPythonInterpretersAsync(ct);
            StatusMessage = string.Empty;
        }
        catch (Exception ex) { StatusMessage = $"刷新失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    // ── Filter helpers ─────────────────────────────────────────────────────

    private void RebuildFilteredPrograms()
    {
        FilteredPrograms.Clear();
        var lower = _programFilter.ToLowerInvariant();
        foreach (var p in AllPrograms.Where(x => string.IsNullOrEmpty(lower) || x.ToLowerInvariant().Contains(lower)))
            FilteredPrograms.Add(p);
    }

    private void RebuildFilteredParamFiles()
    {
        FilteredParamFiles.Clear();
        var lower = _paramFilter.ToLowerInvariant();
        foreach (var f in AllParamFiles.Where(x => string.IsNullOrEmpty(lower) || x.ToLowerInvariant().Contains(lower)))
            FilteredParamFiles.Add(f);
    }

    // ── Command list management ────────────────────────────────────────────

    private async Task AddCommandAsync(CancellationToken ct)
    {
        var cmd = new CommandEntryViewModel
        {
            Order = Commands.Count,
            IncludeBindToNone = ShouldIncludeBindToNone(),
        };
        WireCommandCollections(cmd);
        Commands.Add(cmd);
        SelectedCommand = cmd;
        UpdateCommandOrders();
        await BrowseProgramPathAsync(ct);
    }

    private void RemoveCommand(CommandEntryViewModel? cmd)
    {
        if (cmd == null || Commands.Count == 0) return;
        var idx = Commands.IndexOf(cmd);
        Commands.Remove(cmd);
        SelectedCommand = Commands.Count == 0 ? null : Commands[Math.Min(idx, Commands.Count - 1)];
        UpdateCommandOrders();
    }

    private void MoveUp(CommandEntryViewModel? cmd)
    {
        if (cmd == null) return;
        var idx = Commands.IndexOf(cmd);
        if (idx <= 0) return;
        Commands.Move(idx, idx - 1);
        UpdateCommandOrders();
    }

    private void MoveDown(CommandEntryViewModel? cmd)
    {
        if (cmd == null) return;
        var idx = Commands.IndexOf(cmd);
        if (idx >= Commands.Count - 1) return;
        Commands.Move(idx, idx + 1);
        UpdateCommandOrders();
    }

    private void UpdateCommandOrders()
    {
        for (int i = 0; i < Commands.Count; i++)
            Commands[i].Order = i;
        CommandManager.InvalidateRequerySuggested();
    }

    // ── Param file management (per selected command) ───────────────────────

    private async Task AddParamFileAsync(CancellationToken ct)
    {
        if (_selectedCommand == null || string.IsNullOrWhiteSpace(_selectedAvailableParamFile)) return;
        var sourcePath = _selectedAvailableParamFile.Trim();
        var path = await MaterializeParameterFileToWorkDirAsync(sourcePath, ct);
        if (string.IsNullOrWhiteSpace(path)) return;

        if (!_selectedCommand.ParameterFiles.Contains(path))
            _selectedCommand.ParameterFiles.Add(path);
        _pathLibrary.Remember(TaskPathKind.ParameterFile, sourcePath);
        _selectedCommand.RebuildCommandLine();
    }

    private async Task ApplySelectedProgramAsync(CancellationToken ct)
    {
        if (_selectedCommand == null || string.IsNullOrWhiteSpace(_selectedAvailableProgram)) return;
        _selectedCommand.UsePythonInterpreter = false;
        _selectedCommand.ProgramPath = _selectedAvailableProgram.Trim();
        _pathLibrary.Remember(TaskPathKind.Program, _selectedCommand.ProgramPath);
        await AutoDetectMpirunForProgramAsync(_selectedCommand, ct, updateStatusWhenMissing: false);
    }

    private void RemoveParamFile(string? path)
    {
        if (_selectedCommand == null || string.IsNullOrEmpty(path)) return;
        _selectedCommand.ParameterFiles.Remove(path);
        _selectedCommand.RebuildCommandLine();
    }

    private async Task EditParamFileAsync(string? path, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(path) || !IsSshConnectedSafe())
        {
            StatusMessage = "请先建立 SSH 连接后再编辑文件。";
            return;
        }
        try
        {
            if (!await _ssh.RemoteFileExistsAsync(path, ct))
            {
                StatusMessage = $"远程文件不存在：{path}";
                return;
            }
        }
        catch (Exception ex) { StatusMessage = $"检查文件失败：{ex.Message}"; return; }

        var vm  = new RemoteFileEditorViewModel(_ssh, _prefs, path);
        var win = new RemoteFileEditorView { DataContext = vm };
        if (Application.Current.MainWindow is { } mw) win.Owner = mw;
        await vm.LoadAsync(ct);
        win.ShowDialog();
    }

    // ── Extra args (per selected command) ─────────────────────────────────
    // Track PropertyChanged handlers so they can be unsubscribed on remove.
    private readonly Dictionary<ExtraArgViewModel, PropertyChangedEventHandler> _extraArgHandlers = new();

    private void AddExtraArg()
    {
        if (_selectedCommand == null) return;
        var arg = new ExtraArgViewModel();
        var capturedCmd = _selectedCommand;
        PropertyChangedEventHandler handler = (_, __) => capturedCmd.RebuildCommandLine();
        _extraArgHandlers[arg] = handler;
        arg.PropertyChanged += handler;
        _selectedCommand.ExtraArgs.Add(arg);
    }

    private void RemoveExtraArg(ExtraArgViewModel? arg)
    {
        if (arg == null || _selectedCommand == null) return;
        if (_extraArgHandlers.TryGetValue(arg, out var handler))
        {
            arg.PropertyChanged -= handler;
            _extraArgHandlers.Remove(arg);
        }
        _selectedCommand.ExtraArgs.Remove(arg);
        _selectedCommand.RebuildCommandLine();
    }

    // ── Environment variables (per selected command) ─────────────────────
    private readonly Dictionary<EnvironmentVariableViewModel, PropertyChangedEventHandler> _environmentHandlers = new();

    private void AddEnvironmentVariable()
    {
        if (_selectedCommand == null) return;

        var vm = new EnvironmentVariableDialogViewModel(
            L("CmdBuilder.EnvironmentDialogTitle", "添加环境变量"),
            L("CmdBuilder.EnvironmentDialogPrompt", "输入应用到当前命令的环境变量。"),
            L("CmdBuilder.EnvironmentKey", "变量名，例如 OMP_NUM_THREADS"),
            L("CmdBuilder.EnvironmentValue", "变量值"),
            L("Dialog.Confirm", "确定"),
            L("Dialog.Cancel", "取消"));
        var win = new EnvironmentVariableDialogView { DataContext = vm };
        if (Application.Current.MainWindow is { } mw)
            win.Owner = mw;

        if (win.ShowDialog() != true || !vm.Confirmed)
            return;

        var env = new EnvironmentVariableViewModel(new EnvironmentVariableEntry
        {
            Key = vm.Key.Trim(),
            Value = vm.Value,
        });
        _selectedCommand.EnvironmentVariables.Add(env);
        _selectedCommand.RebuildCommandLine();
    }

    private void RemoveEnvironmentVariable(EnvironmentVariableViewModel? env)
    {
        if (env == null || _selectedCommand == null) return;
        if (_environmentHandlers.TryGetValue(env, out var handler))
        {
            env.PropertyChanged -= handler;
            _environmentHandlers.Remove(env);
        }

        _selectedCommand.EnvironmentVariables.Remove(env);
        _selectedCommand.RebuildCommandLine();
    }

    // ── MPI path auto-detection ────────────────────────────────────────────

    private async Task DetectMpiAsync(CancellationToken ct)
    {
        if (_selectedCommand == null || string.IsNullOrWhiteSpace(_selectedCommand.ProgramPath)) return;
        if (!IsSshConnectedSafe()) { StatusMessage = "请先建立 SSH 连接。"; return; }
        await AutoDetectMpirunForProgramAsync(_selectedCommand, ct, updateStatusWhenMissing: true);
    }

    /// <summary>
    /// Runs <c>ldd &lt;program&gt;</c> on the remote, looks for libmpi.so,
    /// derives the OpenMPI prefix and checks that <prefix>/bin/mpirun exists.
    /// Falls back to <c>which mpirun</c> on failure.
    /// </summary>
    private async Task<string> InferMpirunPathAsync(string programPath, CancellationToken ct)
    {
        if (UseDefaultMpirunForProgram(programPath))
            return await ResolveDefaultMpirunPathAsync(ct);

        // Step 1: ldd
        var (lddOut, _, lddExit) = await _ssh.ExecuteAsync($"ldd {EscapeShell(programPath)} 2>/dev/null", ct);
        if (lddExit == 0 && !string.IsNullOrWhiteSpace(lddOut))
        {
            var mpirun = ParseMpirunFromLdd(lddOut);
            if (!string.IsNullOrEmpty(mpirun))
            {
                // Validate it exists and is executable
                var (_, _, chkExit) = await _ssh.ExecuteAsync($"test -x {EscapeShell(mpirun)}", ct);
                if (chkExit == 0) return mpirun;
            }
        }

        // Step 2: command -v mpirun
        var (whichOut, _, whichExit) = await _ssh.ExecuteAsync("command -v mpirun 2>/dev/null", ct);
        if (whichExit == 0 && !string.IsNullOrWhiteSpace(whichOut))
        {
            var path = whichOut.Trim();
            if (!string.IsNullOrEmpty(path)) return path;
        }

        return string.Empty;
    }

    private async Task<string> ResolveDefaultMpirunPathAsync(CancellationToken ct)
    {
        var (stdout, _, _) = await _ssh.ExecuteAsync("MPIRUN_PATH=$(command -v mpirun 2>/dev/null || true); if [ -n \"$MPIRUN_PATH\" ]; then readlink -f \"$MPIRUN_PATH\" 2>/dev/null || echo \"$MPIRUN_PATH\"; fi", ct);
        var path = stdout.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var (_, _, checkExit) = await _ssh.ExecuteAsync($"test -x {EscapeShell(path)}", ct);
        return checkExit == 0 ? path : string.Empty;
    }

    private static bool UseDefaultMpirunForProgram(string programPath)
    {
        var trimmed = programPath?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return false;

        var extension = GetFileExtension(trimmed);
        return extension.Equals(".sh", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".py", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetFileExtension(string path)
    {
        var fileName = GetFileNameFromPath(path);
        var dotIndex = fileName.LastIndexOf('.');
        return dotIndex > -1 && dotIndex < fileName.Length - 1
            ? fileName[dotIndex..]
            : string.Empty;
    }

    private static string ParseMpirunFromLdd(string lddOutput)
    {
        // Look for lines containing libmpi.so, e.g.:
        //   libmpi.so.40 => /opt/openmpi/lib/libmpi.so.40 (0x00007f...)
        var patterns = new[]
        {
            @"libmpi\.so[^\s]*\s+=>\s+(\S+)",
            @"libmpi_cxx\.so[^\s]*\s+=>\s+(\S+)",
            @"libopen-rte\.so[^\s]*\s+=>\s+(\S+)",
        };

        foreach (var pat in patterns)
        {
            var m = Regex.Match(lddOutput, pat);
            if (!m.Success) continue;

            var libPath = m.Groups[1].Value;
            if (string.IsNullOrEmpty(libPath) || libPath == "not") continue;

            // libPath is something like /opt/openmpi/lib/libmpi.so.40
            var candidate = InferPrefixBin(libPath);
            if (!string.IsNullOrEmpty(candidate)) return candidate;
        }

        return string.Empty;
    }

    /// <summary>
    /// Given a shared library absolute path, traverses up the directory hierarchy to
    /// find a <c>lib</c> or <c>lib64</c> component and derives <c>&lt;prefix&gt;/bin/mpirun</c>.
    /// </summary>
    private static string InferPrefixBin(string libPath)
    {
        var lastSlash = libPath.LastIndexOf('/');
        if (lastSlash <= 0) return string.Empty;

        var dir = libPath[..lastSlash]; // e.g. /opt/openmpi/lib

        var dirSlash = dir.LastIndexOf('/');
        if (dirSlash <= 0) return string.Empty;

        var dirName = dir[(dirSlash + 1)..]; // e.g. "lib" or "lib64"
        if (dirName is "lib" or "lib64")
        {
            var prefix = dir[..dirSlash]; // e.g. /opt/openmpi
            return $"{prefix}/bin/mpirun";
        }

        // One more level up (e.g. /opt/openmpi/lib/openmpi/...)
        var parent = dir[..dirSlash]; // e.g. /opt/openmpi/lib
        var parentSlash = parent.LastIndexOf('/');
        if (parentSlash <= 0) return string.Empty;

        var parentName = parent[(parentSlash + 1)..];
        if (parentName is "lib" or "lib64")
        {
            var prefix = parent[..parentSlash];
            return $"{prefix}/bin/mpirun";
        }

        return string.Empty;
    }

    // ── Test command ───────────────────────────────────────────────────────

    private async Task TestCommandAsync(CancellationToken ct)
    {
        if (_selectedCommand == null || string.IsNullOrWhiteSpace(_selectedCommand.ProgramPath)) return;
        if (!IsSshConnectedSafe()) return;

        IsBusy = true;
        StatusMessage = "测试中…";
        try
        {
            var (stdout, stderr, exitCode) = await _ssh.ExecuteAsync(
                $"{_selectedCommand.ProgramPath} --help 2>&1 | head -20", ct);
            var output = (stdout + stderr).Trim();
            StatusMessage = exitCode == 0
                ? $"命令可访问（退出码 0）：{TruncateText(output, 120)}"
                : $"命令返回退出码 {exitCode}：{TruncateText(output, 120)}";
        }
        catch (Exception ex) { StatusMessage = $"测试失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    private async Task BrowseProgramPathAsync(CancellationToken ct)
    {
        if (_selectedCommand == null) return;
        _selectedCommand.UsePythonInterpreter = false;
        var selectedFile = await OpenTaskPathPickerAsync(
            TaskPathKind.Program,
            AllPrograms
                .Concat(Commands.Select(c => c.ProgramPath))
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            _selectedCommand.ProgramPath,
            ResolveProgramPickerStartPath(_selectedCommand.ProgramPath),
            ct);
        if (string.IsNullOrWhiteSpace(selectedFile)) return;
        _selectedCommand.ProgramPath = selectedFile;
        _pathLibrary.Remember(TaskPathKind.Program, selectedFile);
        await AutoDetectMpirunForProgramAsync(_selectedCommand, ct, updateStatusWhenMissing: false);
    }

    private async Task BrowsePythonInterpreterAsync(CancellationToken ct)
    {
        if (_selectedCommand == null) return;
        var selectedFile = await BrowseRemoteFileAsync(ct, ResolveProgramPickerStartPath(_selectedCommand.PythonInterpreterPath));
        if (string.IsNullOrWhiteSpace(selectedFile)) return;

        _selectedCommand.UsePythonInterpreter = true;
        _selectedCommand.PythonInterpreterPath = selectedFile;
        RememberPythonInterpreter(selectedFile, string.Empty);
        _pathLibrary.Remember(TaskPathKind.Program, selectedFile);
        _selectedCommand.RebuildCommandLine();
    }

    private async Task BrowseParamFileAsync(CancellationToken ct)
    {
        if (_selectedCommand == null) return;
        var selectedFile = await OpenTaskPathPickerAsync(
            TaskPathKind.ParameterFile,
            AllParamFiles
                .Concat(Commands.SelectMany(c => c.ParameterFiles))
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            SelectedAvailableParamFile,
            await ResolveHomeDirectoryAsync(ct),
            ct);
        if (string.IsNullOrWhiteSpace(selectedFile)) return;

        var workCopy = await MaterializeParameterFileToWorkDirAsync(selectedFile, ct);
        if (string.IsNullOrWhiteSpace(workCopy)) return;

        if (!_selectedCommand.ParameterFiles.Contains(workCopy))
            _selectedCommand.ParameterFiles.Add(workCopy);

        _pathLibrary.Remember(TaskPathKind.ParameterFile, selectedFile);
        SelectedAvailableParamFile = workCopy;
        _selectedCommand.RebuildCommandLine();
    }

    private async Task CreateParamFileAsync(CancellationToken ct)
    {
        if (_selectedCommand == null)
            return;

        if (!await EnsureRemoteWorkDirectoryExistsAsync(ct))
            return;

        var vm = new NameInputDialogViewModel(
            title: L("CmdBuilder.NewParamDialogTitle", "新建参数文件"),
            prompt: L("CmdBuilder.NewParamDialogPrompt", "请输入参数文件名："),
            initialValue: string.Empty,
            confirmButtonText: L("CmdBuilder.NewParamDialogConfirm", "新建"),
            cancelButtonText: L("CmdBuilder.NewParamDialogCancel", "取消"));
        var dialog = new NameInputDialogView { DataContext = vm };
        if (Application.Current.MainWindow is { } mainWindow)
            dialog.Owner = mainWindow;
        if (dialog.ShowDialog() != true || !vm.Confirmed)
            return;

        var fileName = vm.InputValue.Trim();
        var validationError = ValidateFileName(fileName);
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            StatusMessage = validationError;
            return;
        }

        var remotePath = BuildWorkDirFilePath(fileName);
        try
        {
            if (await _ssh.RemoteFileExistsAsync(remotePath, ct))
            {
                var overwrite = AppDialogService.ConfirmWarning(
                    L("CmdBuilder.OverwriteParamTitle", "覆盖确认"),
                    string.Format(L("CmdBuilder.OverwriteParamPrompt", "工作目录中已存在同名文件：{0}\n是否覆盖？"), remotePath),
                    confirmButtonText: L("Btn.Confirm", "确定"),
                    cancelButtonText: L("Btn.Cancel", "取消"));
                if (!overwrite)
                {
                    StatusMessage = L("CmdBuilder.OperationCancelled", "已取消。");
                    return;
                }
            }

            await _ssh.WriteTextFileAsync(remotePath, string.Empty, ct);
            if (!_selectedCommand.ParameterFiles.Contains(remotePath))
                _selectedCommand.ParameterFiles.Add(remotePath);
            _pathLibrary.Remember(TaskPathKind.ParameterFile, remotePath);
            _selectedCommand.RebuildCommandLine();
            await EditParamFileAsync(remotePath, ct);
            StatusMessage = string.Format(L("CmdBuilder.ParamCreatedInWorkDir", "已在工作目录创建参数文件副本：{0}"), remotePath);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(L("CmdBuilder.ParamCreateFailed", "创建参数文件失败：{0}"), ex.Message);
        }
    }

    private async Task AutoDetectMpirunForProgramAsync(
        CommandEntryViewModel command,
        CancellationToken ct,
        bool updateStatusWhenMissing)
    {
        if (command == null || string.IsNullOrWhiteSpace(command.ProgramPath) || !IsSshConnectedSafe())
            return;

        IsBusy = true;
        StatusMessage = UseDefaultMpirunForProgram(command.ProgramPath)
            ? L("CmdBuilder.MpiDetectingDefault", "正在获取远程默认 mpirun 路径…")
            : L("CmdBuilder.MpiDetectingFromProgram", "正在通过程序依赖推断 mpirun 路径…");
        try
        {
            var mpirun = await InferMpirunPathAsync(command.ProgramPath, ct);
            if (!string.IsNullOrWhiteSpace(mpirun))
            {
                command.MpirunPath = mpirun;
                command.RebuildCommandLine();
                StatusMessage = string.Format(L("CmdBuilder.MpiDetected", "已推断 mpirun 路径：{0}"), mpirun);
                RegenerateSbatch();
            }
            else if (updateStatusWhenMissing)
            {
                StatusMessage = L("CmdBuilder.MpiDetectFailed", "未能自动推断 mpirun 路径，请手动填写。");
            }
        }
        catch (Exception ex)
        {
            if (updateStatusWhenMissing)
                StatusMessage = string.Format(L("CmdBuilder.MpiDetectException", "MPI 路径推断失败：{0}"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<string?> BrowseRemoteFileAsync(CancellationToken ct, string? startPath = null)
    {
        if (!IsSshConnectedSafe())
        {
            StatusMessage = L("Task.RequireConnectionForBrowse", "请先建立 SSH 连接后再浏览远程目录。");
            return null;
        }

        var homeDir = await ResolveHomeDirectoryAsync(ct);
        var initialPath = string.IsNullOrWhiteSpace(startPath) ? homeDir : NormalizeRemotePath(startPath);
        var vm = new RemoteFilePickerViewModel(_ssh, initialPath, restrictToHomeScope: false);
        var win = new RemoteFilePickerView { DataContext = vm };
        if (Application.Current.MainWindow is { } mainWin) win.Owner = mainWin;

        await vm.LoadInitialAsync(ct);
        return win.ShowDialog() == true ? vm.ResultPath : null;
    }

    private async Task<string?> OpenTaskPathPickerAsync(
        TaskPathKind kind,
        IEnumerable<string> candidatePaths,
        string? currentPath,
        string? remoteBrowseStartPath,
        CancellationToken ct)
    {
        var vm = new TaskPathPickerViewModel(kind, candidatePaths, currentPath, _pathLibrary);
        var win = new TaskPathPickerView { DataContext = vm };
        if (Application.Current.MainWindow is { } mainWin)
            win.Owner = mainWin;

        if (win.ShowDialog() != true)
            return null;

        if (vm.BrowseRequested)
            return await BrowseRemoteFileAsync(ct, remoteBrowseStartPath);

        return vm.ConfirmedPath;
    }

    private async Task LoadPythonInterpretersAsync(CancellationToken ct)
    {
        PythonInterpreters.Clear();

        foreach (var savedPath in Commands
                     .Select(command => command.PythonInterpreterPath)
                     .Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            RememberPythonInterpreter(savedPath!, string.Empty);
        }

        if (!IsSshConnectedSafe())
            return;

        try
        {
            const string command =
                "for p in python3 python python3.13 python3.12 python3.11 python3.10 python3.9 python3.8 /usr/bin/python3 /usr/local/bin/python3; do " +
                "if command -v \"$p\" >/dev/null 2>&1; then r=$(command -v \"$p\"); v=$($r --version 2>&1 | head -1); printf '%s|%s\\n' \"$r\" \"$v\"; fi; " +
                "done | awk -F'|' '!seen[$1]++'";
            var (stdout, _, exitCode) = await _ssh.ExecuteAsync(command, ct);
            if (exitCode != 0)
                return;

            foreach (var line in stdout.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('|', 2);
                var path = parts.ElementAtOrDefault(0)?.Trim() ?? string.Empty;
                var version = parts.ElementAtOrDefault(1)?.Trim() ?? string.Empty;
                RememberPythonInterpreter(path, version);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandBuilderViewModel.LoadPythonInterpretersAsync] {ex.Message}");
        }
    }

    private void RememberPythonInterpreter(string path, string version)
    {
        var normalized = NormalizeRemotePath(path);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        if (PythonInterpreters.Any(x => string.Equals(x.Path, normalized, StringComparison.Ordinal)))
            return;

        PythonInterpreters.Add(new PythonInterpreterOption(normalized, string.IsNullOrWhiteSpace(version)
            ? normalized
            : $"{version}  ·  {normalized}"));
    }

    private async Task<string> ResolveHomeDirectoryAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_homeDirectory))
            return _homeDirectory;

        try
        {
            var home = await _ssh.GetHomeDirectoryAsync(ct);
            if (!string.IsNullOrWhiteSpace(home))
            {
                _homeDirectory = home.TrimEnd('/');
                return _homeDirectory;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommandBuilderViewModel.ResolveHomeDirectoryAsync] {ex.Message}");
        }

        _homeDirectory = "~";
        return _homeDirectory;
    }

    private async Task<string?> MaterializeParameterFileToWorkDirAsync(string sourcePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return null;
        if (string.IsNullOrWhiteSpace(_remoteWorkDir))
        {
            StatusMessage = L("CmdBuilder.MissingWorkDir", "请先配置远程工作目录。");
            return null;
        }
        if (!await EnsureRemoteWorkDirectoryExistsAsync(ct))
            return null;

        var normalizedSource = sourcePath.Trim();
        var fileName = GetFileNameFromPath(normalizedSource);
        var validationError = ValidateFileName(fileName);
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            StatusMessage = validationError;
            return null;
        }

        var targetPath = BuildWorkDirFilePath(fileName);
        try
        {
            var targetExists = await _ssh.RemoteFileExistsAsync(targetPath, ct);
            var samePath = string.Equals(
                NormalizeRemotePath(normalizedSource),
                NormalizeRemotePath(targetPath),
                StringComparison.Ordinal);

            if (targetExists && !samePath)
            {
                var overwrite = AppDialogService.ConfirmWarning(
                    L("CmdBuilder.OverwriteParamTitle", "覆盖确认"),
                    string.Format(L("CmdBuilder.OverwriteParamPrompt", "工作目录中已存在同名文件：{0}\n是否覆盖？"), targetPath),
                    confirmButtonText: L("Btn.Confirm", "确定"),
                    cancelButtonText: L("Btn.Cancel", "取消"));
                if (!overwrite)
                {
                    StatusMessage = L("CmdBuilder.OperationCancelled", "已取消。");
                    return null;
                }
            }

            if (!samePath)
            {
                var escapedSource = EscapeShell(normalizedSource);
                var escapedTarget = EscapeShell(targetPath);
                var (_, copyErr, copyExit) = await _ssh.ExecuteAsync($"cp -- {escapedSource} {escapedTarget}", ct);
                if (copyExit != 0)
                {
                    var detail = string.IsNullOrWhiteSpace(copyErr)
                        ? L("CmdBuilder.ParamCopyFailedNoDetail", "复制参数文件失败。")
                        : copyErr.Trim();
                    StatusMessage = string.Format(L("CmdBuilder.ParamCopyFailed", "复制参数文件失败：{0}"), detail);
                    return null;
                }
            }

            StatusMessage = string.Format(
                L("CmdBuilder.ParamCopiedToWorkDir", "已复制参数文件到工作目录副本：{0}"),
                targetPath);
            return targetPath;
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(L("CmdBuilder.ParamCopyFailed", "复制参数文件失败：{0}"), ex.Message);
            return null;
        }
    }

    private async Task<bool> EnsureRemoteWorkDirectoryExistsAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_remoteWorkDir))
        {
            StatusMessage = L("CmdBuilder.MissingWorkDir", "请先配置远程工作目录。");
            return false;
        }

        try
        {
            var (_, dirErr, dirExit) = await _ssh.ExecuteAsync($"mkdir -p {EscapeShell(_remoteWorkDir)}", ct);
            if (dirExit != 0)
            {
                var detail = string.IsNullOrWhiteSpace(dirErr) ? L("CmdBuilder.WorkDirPrepareFailed", "准备工作目录失败。") : dirErr.Trim();
                StatusMessage = string.Format(L("CmdBuilder.WorkDirPrepareFailedWithDetail", "准备工作目录失败：{0}"), detail);
                return false;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(L("CmdBuilder.WorkDirPrepareFailedWithDetail", "准备工作目录失败：{0}"), ex.Message);
            return false;
        }

        return true;
    }

    private string BuildWorkDirFilePath(string fileName)
    {
        var normalizedWorkDir = NormalizeRemotePath(_remoteWorkDir);
        if (normalizedWorkDir == "/")
            return $"/{fileName}";
        return $"{normalizedWorkDir.TrimEnd('/')}/{fileName}";
    }

    private void ApplyMpiBindingPolicy()
    {
        var includeBindToNone = ShouldIncludeBindToNone();
        foreach (var command in Commands)
            command.IncludeBindToNone = includeBindToNone;
    }

    private bool ShouldIncludeBindToNone()
        => string.IsNullOrWhiteSpace(_sbatchCpuCount);

    private void ApplyInitialSbatchOptions(string taskId, SbatchJobOptions? initialSbatchOptions, string? initialSbatch)
    {
        if (initialSbatchOptions != null)
        {
            _sbatchJobName = initialSbatchOptions.JobName ?? string.Empty;
            _sbatchPartition = initialSbatchOptions.Partition ?? string.Empty;
            _sbatchNodes = NormalizeNodes(initialSbatchOptions.Nodes);
            _sbatchTaskCount = NormalizeTaskCount(initialSbatchOptions.TaskCount);
            _sbatchCpuCount = initialSbatchOptions.CpuCount ?? string.Empty;
            _sbatchGpuCount = NormalizeGpuCount(initialSbatchOptions.GpuCount);
            SetSbatchTimeLimit(initialSbatchOptions.TimeLimit, regenerate: false);
            _sbatchAccount = NormalizeAccount(initialSbatchOptions.Account);
            _sbatchExclusive = initialSbatchOptions.Exclusive;
            return;
        }

        _sbatchNodes = DefaultNodes;
        _sbatchTaskCount = DefaultTaskCount;
        _sbatchCpuCount = string.Empty;
        _sbatchGpuCount = string.Empty;
        SetSbatchTimeLimit(DefaultTimeLimit, regenerate: false);
        _sbatchAccount = DefaultAccount;
        _sbatchExclusive = false;

        var defaultJobName = string.IsNullOrWhiteSpace(taskId) ? "job" : taskId.Trim();
        _sbatchJobName = defaultJobName;
        _sbatchPartition = string.Empty;

        if (!string.IsNullOrWhiteSpace(initialSbatch))
            ParseSbatchOptionsFromScript(initialSbatch);
    }

    // ── Save & Apply ───────────────────────────────────────────────────────

    private void SaveAndApply()
    {
        if (!ValidateSbatchContent()) return;

        // Rebuild all command lines before confirming
        foreach (var cmd in Commands)
            if (cmd.UsePythonInterpreter || !string.IsNullOrWhiteSpace(cmd.ProgramPath))
                cmd.RebuildCommandLine();

        _pathLibrary.RememberCommands(GetResultCommands());
        Confirmed = true;
    }

    // ── Snapshot for caller ────────────────────────────────────────────────

    /// <summary>Returns the updated command entries to apply back to the task unit.</summary>
    public IReadOnlyList<CommandEntry> GetResultCommands()
        => Commands.Select(c => c.ToModel()).ToList();

    /// <summary>Returns the current sbatch content.</summary>
    public string GetResultSbatch() => _sbatchContent;

    public SbatchJobOptions GetResultSbatchOptions() => new()
    {
        JobName = _sbatchJobName.Trim(),
        Partition = _sbatchPartition.Trim(),
        Nodes = NormalizeNodes(_sbatchNodes),
        TaskCount = NormalizeTaskCount(_sbatchTaskCount),
        CpuCount = _sbatchCpuCount.Trim(),
        GpuCount = NormalizeGpuCount(_sbatchGpuCount),
        TimeLimit = NormalizeTimeLimit(_sbatchTimeLimit),
        Account = NormalizeAccount(_sbatchAccount),
        Exclusive = _sbatchExclusive,
    };

    // ── sbatch generation ──────────────────────────────────────────────────

    private void ParseSbatchOptionsFromScript(string script)
    {
        var lines = script.Replace("\r\n", "\n").Split('\n');
        if (TryReadDirective(lines, "--job-name", out var jobName))
            _sbatchJobName = jobName;
        if (TryReadDirective(lines, "--partition", out var partition))
            _sbatchPartition = partition;
        if (TryReadDirective(lines, "--nodes", out var nodes))
            _sbatchNodes = nodes;
        if (TryReadDirective(lines, "--ntasks", out var taskCount))
            _sbatchTaskCount = taskCount;
        if (TryReadDirective(lines, "--cpus-per-task", out var cpus))
            _sbatchCpuCount = cpus;
        if (TryReadDirective(lines, "--gres", out var gres))
            _sbatchGpuCount = ParseGpuCountFromGres(gres);
        if (TryReadDirective(lines, "--time", out var timeLimit))
            SetSbatchTimeLimit(timeLimit, regenerate: false);
        else
            SetSbatchTimeLimit(string.Empty, regenerate: false);
        if (TryReadDirective(lines, "--account", out var account))
            _sbatchAccount = account;

        _sbatchExclusive = lines.Any(line => line.Trim().Equals("#SBATCH --exclusive", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryReadDirective(IEnumerable<string> lines, string directiveName, out string value)
    {
        foreach (var line in lines)
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

    /// <summary>Rebuilds the sbatch content from the current commands list.</summary>
    public void RegenerateSbatch()
    {
        var firstProg = Commands.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.ProgramPath))?.ProgramPath ?? string.Empty;
        var progName  = GetFileNameFromPath(firstProg);
        var generatedJobName = string.IsNullOrEmpty(_taskId)
            ? (string.IsNullOrEmpty(progName) ? "job" : progName)
            : (string.IsNullOrEmpty(progName) ? _taskId : $"{_taskId}_{progName}");
        var jobName = string.IsNullOrWhiteSpace(_sbatchJobName) ? generatedJobName : _sbatchJobName.Trim();

        var workDir = string.IsNullOrEmpty(_remoteWorkDir) ? "/tmp/job" : _remoteWorkDir;
        var stdout  = $"{workDir}/logs/job.out";
        var stderr  = $"{workDir}/logs/job.err";
        var nodes = NormalizeNodes(_sbatchNodes);
        var taskCount = NormalizeTaskCount(_sbatchTaskCount);
        var timeLimit = NormalizeTimeLimit(_sbatchTimeLimit);
        var account = NormalizeAccount(_sbatchAccount);
        var partition = _sbatchPartition.Trim();
        var cpuCount = _sbatchCpuCount.Trim();
        var gpuCount = NormalizeGpuCount(_sbatchGpuCount);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("#!/bin/bash -l");
        sb.AppendLine($"#SBATCH --job-name={jobName}");
        sb.AppendLine($"#SBATCH --partition={partition}");
        sb.AppendLine($"#SBATCH --nodes={nodes}");
        sb.AppendLine($"#SBATCH --ntasks={taskCount}");
        if (!string.IsNullOrWhiteSpace(cpuCount))
            sb.AppendLine($"#SBATCH --cpus-per-task={cpuCount}");
        if (!string.IsNullOrWhiteSpace(gpuCount))
            sb.AppendLine($"#SBATCH --gres=gpu:{gpuCount}");
        if (!string.IsNullOrWhiteSpace(timeLimit))
            sb.AppendLine($"#SBATCH --time={timeLimit}");
        sb.AppendLine($"#SBATCH --account={account}");
        if (_sbatchExclusive)
            sb.AppendLine("#SBATCH --exclusive");
        sb.AppendLine($"#SBATCH --output={stdout}");
        sb.AppendLine($"#SBATCH --error={stderr}");
        sb.AppendLine($"#SBATCH --chdir={workDir}");
        sb.AppendLine();
        sb.AppendLine($"cd {workDir}");
        sb.AppendLine($"IFACE_NAME=$(ip route get {InterfaceProbeIp} | awk '{{print $3}}')");
        sb.AppendLine();
        sb.AppendLine($"echo \"Starting job {jobName} at $(date)\"");
        sb.AppendLine();

        foreach (var cmd in Commands.Where(c => !string.IsNullOrWhiteSpace(c.CommandLine)))
        {
            sb.AppendLine(cmd.CommandLine);
        }

        sb.AppendLine();
        sb.AppendLine("echo \"Job finished at $(date)\"");

        SbatchContent = sb.ToString();
    }

    private string BuildDefaultSbatch()
    {
        var workDir = string.IsNullOrEmpty(_remoteWorkDir) ? "/tmp/job" : _remoteWorkDir;
        var jobName = string.IsNullOrWhiteSpace(_sbatchJobName)
            ? (string.IsNullOrEmpty(_taskId) ? "job" : _taskId)
            : _sbatchJobName.Trim();
        var nodes = NormalizeNodes(_sbatchNodes);
        var taskCount = NormalizeTaskCount(_sbatchTaskCount);
        var timeLimit = NormalizeTimeLimit(_sbatchTimeLimit);
        var account = NormalizeAccount(_sbatchAccount);
        var partition = _sbatchPartition.Trim();
        var cpuCount = _sbatchCpuCount.Trim();
        var gpuCount = NormalizeGpuCount(_sbatchGpuCount);
        return
            "#!/bin/bash -l\n" +
            $"#SBATCH --job-name={jobName}\n" +
            $"#SBATCH --partition={partition}\n" +
            $"#SBATCH --nodes={nodes}\n" +
            $"#SBATCH --ntasks={taskCount}\n" +
            (string.IsNullOrWhiteSpace(cpuCount) ? string.Empty : $"#SBATCH --cpus-per-task={cpuCount}\n") +
            (string.IsNullOrWhiteSpace(gpuCount) ? string.Empty : $"#SBATCH --gres=gpu:{gpuCount}\n") +
            (string.IsNullOrWhiteSpace(timeLimit) ? string.Empty : $"#SBATCH --time={timeLimit}\n") +
            $"#SBATCH --account={account}\n" +
            (_sbatchExclusive ? "#SBATCH --exclusive\n" : string.Empty) +
            $"#SBATCH --output={workDir}/logs/job.out\n" +
            $"#SBATCH --error={workDir}/logs/job.err\n" +
            $"#SBATCH --chdir={workDir}\n\n" +
            $"cd {workDir}\n" +
            $"IFACE_NAME=$(ip route get {InterfaceProbeIp} | awk '{{print $3}}')\n\n" +
            $"echo \"Starting job {jobName} at $(date)\"\n\n" +
            "# commands here\n\n" +
            "echo \"Job finished at $(date)\"\n";
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private void WireCommandCollections(CommandEntryViewModel cmd)
    {
        cmd.PropertyChanged += OnCommandPropertyChanged;
        cmd.ParameterFiles.CollectionChanged += (_, __) =>
        {
            cmd.RebuildCommandLine();
            NotifySelectedCommandValidationChanged(cmd);
        };
        cmd.ExtraArgs.CollectionChanged += (_, __) => cmd.RebuildCommandLine();
        foreach (var env in cmd.EnvironmentVariables)
            TrackEnvironmentVariable(cmd, env);
        cmd.EnvironmentVariables.CollectionChanged += (_, args) =>
        {
            if (args.NewItems != null)
            {
                foreach (var env in args.NewItems.OfType<EnvironmentVariableViewModel>())
                    TrackEnvironmentVariable(cmd, env);
            }

            if (args.OldItems != null)
            {
                foreach (var env in args.OldItems.OfType<EnvironmentVariableViewModel>())
                    UntrackEnvironmentVariable(env);
            }

            cmd.RebuildCommandLine();
        };
    }

    private void TrackEnvironmentVariable(CommandEntryViewModel command, EnvironmentVariableViewModel env)
    {
        if (_environmentHandlers.ContainsKey(env))
            return;

        PropertyChangedEventHandler handler = (_, __) => command.RebuildCommandLine();
        _environmentHandlers[env] = handler;
        env.PropertyChanged += handler;
    }

    private void UntrackEnvironmentVariable(EnvironmentVariableViewModel env)
    {
        if (!_environmentHandlers.TryGetValue(env, out var handler))
            return;

        env.PropertyChanged -= handler;
        _environmentHandlers.Remove(env);
    }

    private void OnCommandPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CommandEntryViewModel command)
            return;

        if (e.PropertyName is nameof(CommandEntryViewModel.ProgramPath)
            or nameof(CommandEntryViewModel.MpirunPath)
            or nameof(CommandEntryViewModel.UsePythonInterpreter)
            or nameof(CommandEntryViewModel.PythonInterpreterPath))
        {
            NotifySelectedCommandValidationChanged(command);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void NotifySelectedCommandValidationChanged(CommandEntryViewModel command)
    {
        if (ReferenceEquals(command, _selectedCommand))
            OnPropertyChanged(nameof(SelectedCommandValidationSummary));
    }

    private string BuildSelectedCommandValidationSummary()
    {
        var command = _selectedCommand;
        if (command == null)
            return string.Empty;

        var issues = new List<string>();
        if (command.UsePythonInterpreter)
        {
            if (string.IsNullOrWhiteSpace(command.PythonInterpreterPath))
                issues.Add(L("CmdBuilder.ValidationPythonMissing", "请选择 Python 解释器。"));
        }
        else if (string.IsNullOrWhiteSpace(command.ProgramPath))
        {
            issues.Add(L("CmdBuilder.ValidationProgramMissing", "请选择程序路径。"));
        }

        if (issues.Count > 0)
            return string.Join(" ", issues);

        var executionHint = string.IsNullOrWhiteSpace(command.MpirunPath)
            ? L("CmdBuilder.ValidationReadySerial", "结构完整；当前按非 MPI 命令生成。需要 MPI 时请自动探测或手动填写 mpirun。")
            : L("CmdBuilder.ValidationReadyMpi", "结构完整；已指定 MPI 启动程序。");
        var parameterHint = command.ParameterFiles.Count == 0
            ? L("CmdBuilder.ValidationReadyNoParam", "结构完整；未附加参数文件，将只运行程序和额外参数。")
            : string.Empty;
        return string.Join(" ", new[] { executionHint, parameterHint }.Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static string EscapeShell(string arg)
        => "'" + arg.Replace("'", "'\\''") + "'";

    private static string NormalizeRemotePath(string? path)
    {
        var normalized = (path ?? string.Empty).Trim().Replace('\\', '/');
        if (normalized == "/")
            return "/";
        return normalized.TrimEnd('/');
    }

    private static string TruncateText(string text, int maxLen)
        => text.Length <= maxLen ? text : text[..maxLen] + "…";

    private void SetSbatchTimeLimit(string? value, bool regenerate = true)
    {
        var normalized = NormalizeTimeLimit(value);
        var changed = SetField(ref _sbatchTimeLimit, normalized);
        SyncTimeSelectionsFromLimit(normalized);
        if (changed && regenerate)
            RegenerateSbatch();
    }

    private void UpdateTimeLimitFromSelections()
    {
        if (_isSyncingTimeSelection)
            return;

        // Slurm time here is intentionally normalized to whole-day granularity for the Year/Month/Day picker UI.
        // We map year/month with fixed factors (1 year = 365 days, 1 month = 30 days) to keep script generation stable.
        var totalDays = (_sbatchTimeYears * 365) + (_sbatchTimeMonths * 30) + _sbatchTimeDays;
        var timeLimit = totalDays > 0
            ? $"{totalDays}-00:00:00"
            : string.Empty;

        if (SetField(ref _sbatchTimeLimit, timeLimit))
            RegenerateSbatch();
    }

    private void SyncTimeSelectionsFromLimit(string? timeLimit)
    {
        _isSyncingTimeSelection = true;
        try
        {
            var totalDays = TryParseTotalDays(timeLimit);
            SetField(ref _sbatchTimeYears, totalDays / 365);
            totalDays %= 365;
            SetField(ref _sbatchTimeMonths, totalDays / 30);
            totalDays %= 30;
            SetField(ref _sbatchTimeDays, totalDays);
        }
        finally
        {
            _isSyncingTimeSelection = false;
        }
    }

    private static int TryParseTotalDays(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        var dayDashIndex = value.IndexOf('-', StringComparison.Ordinal);
        if (dayDashIndex > 0
            && int.TryParse(value[..dayDashIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dayPart)
            && dayPart >= 0)
            return dayPart;

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var plainDay) && plainDay >= 0
            ? plainDay
            : 0;
    }

    private static string NormalizeNodes(string? value)
        => string.IsNullOrWhiteSpace(value) ? DefaultNodes : value.Trim();

    private static string NormalizeTaskCount(string? value)
        => string.IsNullOrWhiteSpace(value) ? DefaultTaskCount : value.Trim();

    private static string NormalizeGpuCount(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0
            ? count.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private static string NormalizeTimeLimit(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string NormalizeAccount(string? value)
        => string.IsNullOrWhiteSpace(value) ? DefaultAccount : value.Trim();

    /// <summary>Safely extracts the file name component from a Unix path.</summary>
    private static string GetFileNameFromPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var slashIdx = path.LastIndexOf('/');
        return slashIdx >= 0 ? path[(slashIdx + 1)..] : path;
    }

    private static string? ValidateFileName(string inputName)
    {
        var name = inputName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            return L("CmdBuilder.ParamNameEmpty", "参数文件名不能为空。");
        if (name == "." || name == ".." || name.Contains("..", StringComparison.Ordinal))
            return L("CmdBuilder.ParamNameTraversal", "参数文件名不允许包含路径穿越片段（如 ..）。");
        if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
            return L("CmdBuilder.ParamNameSeparator", "参数文件名不能包含路径分隔符。");
        if (name.Any(char.IsControl))
            return L("CmdBuilder.ParamNameInvalidChar", "参数文件名包含非法字符。");
        return null;
    }

    private bool ValidateSbatchContent()
    {
        var script = SbatchContent ?? string.Empty;
        if (string.IsNullOrWhiteSpace(script))
        {
            StatusMessage = L("CmdBuilder.SbatchEmptyError", "sbatch 脚本内容不能为空，请先填写脚本后再保存应用。");
            return false;
        }

        var normalized = script.Replace("\r\n", "\n");
        var firstNonEmptyLine = normalized
            .Split('\n')
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        var hasShebang = firstNonEmptyLine != null && firstNonEmptyLine.StartsWith("#!", StringComparison.Ordinal);
        if (!hasShebang)
        {
            StatusMessage = L("CmdBuilder.SbatchMissingShebangError", "sbatch 脚本缺少 shebang（例如 #!/bin/bash），请先修正后再保存应用。");
            return false;
        }

        return true;
    }

    private bool CanRunSshCommand()
        => !IsBusy && IsSshConnectedSafe();

    private bool IsSshConnectedSafe()
    {
        try
        {
            return _ssh.IsConnected;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static string ResolveProgramPickerStartPath(string? currentProgramPath)
    {
        if (string.IsNullOrWhiteSpace(currentProgramPath))
            return string.Empty;

        var normalized = NormalizeRemotePath(currentProgramPath);
        var idx = normalized.LastIndexOf('/');
        if (idx <= 0)
            return "/";
        return normalized[..idx];
    }

    private async Task LoadQueueMetadataAsync(CancellationToken ct)
    {
        if (!IsSshConnectedSafe())
            return;

        try
        {
            var (stdout, stderr, exitCode) = await _ssh.ExecuteAsync("sinfo --noheader --format=\"%P|%G|%m|%c\"", ct);
            var parsed = exitCode == 0 ? ParseQueueMetadata(stdout) : new List<QueueMetadata>();
            if (exitCode != 0 || parsed.Count == 0)
            {
                var fallback = await TryLoadQueueNamesAsync(ct);
                if (fallback.Count == 0)
                {
                    if (!string.IsNullOrWhiteSpace(stderr))
                        StatusMessage = string.Format(L("CmdBuilder.QueueLoadFailed", "队列信息加载失败：{0}"), stderr.Trim());
                    return;
                }

                parsed = fallback
                    .Select(name => new QueueMetadata(name))
                    .ToList();
            }

            AvailableQueues.Clear();
            _queueMetadataMap.Clear();
            foreach (var item in parsed.OrderBy(q => q.Name, StringComparer.Ordinal))
            {
                AvailableQueues.Add(item.Name);
                _queueMetadataMap[item.Name] = item;
            }

            if (!string.IsNullOrWhiteSpace(_sbatchPartition)
                && !AvailableQueues.Contains(_sbatchPartition, StringComparer.Ordinal))
            {
                AvailableQueues.Add(_sbatchPartition);
            }

            OnPropertyChanged(nameof(HasSelectedQueueMetadata));
            OnPropertyChanged(nameof(SelectedQueueMetadataSummary));
            OnPropertyChanged(nameof(SelectedQueueSupportsGpu));
            RebuildGpuCountOptions();
            if (GetSelectedQueueMetadata() is { HasGpu: false })
                SbatchGpuCount = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(L("CmdBuilder.QueueLoadFailed", "队列信息加载失败：{0}"), ex.Message);
        }
    }

    private async Task<IReadOnlyList<string>> TryLoadQueueNamesAsync(CancellationToken ct)
    {
        try
        {
            var (stdout, _, exitCode) = await _ssh.ExecuteAsync("sinfo --noheader --format=\"%P\"", ct);
            if (exitCode != 0)
                return Array.Empty<string>();

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
            return Array.Empty<string>();
        }
    }

    private static List<QueueMetadata> ParseQueueMetadata(string raw)
    {
        var map = new Dictionary<string, QueueMetadata>(StringComparer.Ordinal);
        foreach (var line in raw.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var parts = line.Split('|');
            if (parts.Length < 4)
                continue;

            var name = parts[0].Trim().TrimEnd('*');
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var gres = parts[1].Trim();
            var memoryText = parts[2].Trim();
            var cpuText = parts[3].Trim();
            var hasGpu = GpuGresRegex.IsMatch(gres);
            var gpuCount = TryParseGpuCount(gres);
            var memoryMb = TryParseLeadingInt(memoryText);
            var cpuCores = TryParseLeadingInt(cpuText);

            if (!map.TryGetValue(name, out var meta))
            {
                meta = new QueueMetadata(name);
                map[name] = meta;
            }

            meta.HasGpu |= hasGpu;
            if (gpuCount.HasValue && (!meta.MaxGpuCount.HasValue || gpuCount.Value > meta.MaxGpuCount.Value))
                meta.MaxGpuCount = gpuCount.Value;
            if (memoryMb.HasValue && (!meta.MemoryMb.HasValue || memoryMb.Value > meta.MemoryMb.Value))
                meta.MemoryMb = memoryMb.Value;
            if (cpuCores.HasValue && (!meta.CpuCores.HasValue || cpuCores.Value > meta.CpuCores.Value))
                meta.CpuCores = cpuCores.Value;
        }

        return map.Values.ToList();
    }

    private void RebuildGpuCountOptions()
    {
        var previous = NormalizeGpuCount(_sbatchGpuCount);
        var maxGpuCount = Math.Max(GetSelectedQueueMetadata()?.MaxGpuCount ?? 0, 8);
        if (int.TryParse(previous, NumberStyles.Integer, CultureInfo.InvariantCulture, out var selectedCount))
            maxGpuCount = Math.Max(maxGpuCount, selectedCount);

        SbatchGpuCountOptions.Clear();
        SbatchGpuCountOptions.Add(new GpuCountOption(string.Empty, L("CmdBuilder.SbatchGpuNone", "不使用 GPU")));
        for (var i = 1; i <= maxGpuCount; i++)
            SbatchGpuCountOptions.Add(new GpuCountOption(i.ToString(CultureInfo.InvariantCulture), i.ToString(CultureInfo.InvariantCulture)));

        if (!string.Equals(previous, _sbatchGpuCount, StringComparison.Ordinal))
            _sbatchGpuCount = previous;
        OnPropertyChanged(nameof(SbatchGpuCount));
    }

    private QueueMetadata? GetSelectedQueueMetadata()
    {
        var key = _sbatchPartition?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
            return null;
        return _queueMetadataMap.GetValueOrDefault(key);
    }

    private string BuildSelectedQueueMetadataSummary()
    {
        var meta = GetSelectedQueueMetadata();
        if (meta == null)
            return L("CmdBuilder.QueueInfoUnavailable", "未获取到当前队列的资源信息。");

        var gpuText = meta.HasGpu
            ? L("CmdBuilder.QueueInfoGpuYes", "有 GPU")
            : L("CmdBuilder.QueueInfoGpuNo", "无 GPU");
        var memoryText = meta.MemoryMb.HasValue
            ? string.Format(CultureInfo.InvariantCulture, "{0} MB", meta.MemoryMb.Value)
            : L("CmdBuilder.QueueInfoUnknown", "未知");
        var cpuText = meta.CpuCores.HasValue
            ? meta.CpuCores.Value.ToString(CultureInfo.InvariantCulture)
            : L("CmdBuilder.QueueInfoUnknown", "未知");

        return string.Format(
            L("CmdBuilder.QueueInfoTooltipFormat", "队列：{0}\nGPU：{1}\n内存：{2}\nCPU 核心：{3}"),
            meta.Name,
            gpuText,
            memoryText,
            cpuText);
    }

    private static int? TryParseLeadingInt(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var m = LeadingIntRegex.Match(value);
        if (!m.Success)
            return null;
        return int.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static int? TryParseGpuCount(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var match = GpuCountRegex.Match(value);
        if (!match.Success)
            return GpuGresRegex.IsMatch(value) ? 1 : null;

        return int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : null;
    }

    private static string ParseGpuCountFromGres(string value)
        => TryParseGpuCount(value)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private sealed class QueueMetadata(string name)
    {
        public string Name { get; } = name;
        public bool HasGpu { get; set; }
        public int? MaxGpuCount { get; set; }
        public int? MemoryMb { get; set; }
        public int? CpuCores { get; set; }
    }

    public sealed class GpuCountOption(string value, string displayName)
    {
        public string Value { get; } = value;
        public string DisplayName { get; } = displayName;
    }

    public sealed class PythonInterpreterOption(string path, string displayName)
    {
        public string Path { get; } = path;
        public string DisplayName { get; } = displayName;
    }

    private static string L(string key, string fallback)
        => Application.Current?.TryFindResource(key) as string ?? fallback;
}
