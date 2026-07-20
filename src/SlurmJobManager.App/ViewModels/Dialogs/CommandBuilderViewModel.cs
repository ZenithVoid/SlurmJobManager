using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using SlurmJobManager.App.Services;
using SlurmJobManager.App.ViewModels;
using SlurmJobManager.App.Views;
using SlurmJobManager.App.Views.Dialogs;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Models;

namespace SlurmJobManager.App.ViewModels.Dialogs;

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
    private const string RemoteParamDir = "/env/preprocess/out/config";
    private const string InterfaceProbeIp = "10.10.10.202";
    private const string DefaultNodes = "1";
    private const string DefaultTaskCount = "1";
    private const string DefaultTimeLimit = "";
    private const string DefaultAccount = "preproc";

    private readonly ISshClientService _ssh;
    private readonly AppPreferencesService _prefs;
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
    public ObservableCollection<string> AvailableQueues { get; } = new();
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
    public ICommand DetectMpiCommand        { get; }
    public ICommand TestCommandCommand      { get; }
    public ICommand BrowseProgramPathCommand { get; }
    public ICommand BrowseParamFileCommand   { get; }
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

        // Populate commands from the task unit
        if (initialCommands != null)
        {
            foreach (var ce in initialCommands)
                Commands.Add(new CommandEntryViewModel(ce));
        }

        // Ensure at least one command entry
        if (Commands.Count == 0)
            Commands.Add(new CommandEntryViewModel());

        SelectedCommand = Commands[0];

        ApplyInitialSbatchOptions(taskId, initialSbatchOptions, initialSbatch);
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
        AddCommandCommand      = new RelayCommand(AddCommand);
        RemoveCommandCommand   = new RelayCommand<CommandEntryViewModel>(RemoveCommand, c => c != null && Commands.Count > 1);
        MoveUpCommand          = new RelayCommand<CommandEntryViewModel>(MoveUp,   c => c != null && Commands.IndexOf(c) > 0);
        MoveDownCommand        = new RelayCommand<CommandEntryViewModel>(MoveDown, c => c != null && Commands.IndexOf(c) < Commands.Count - 1);
        ApplySelectedProgramCommand = new AsyncRelayCommand(ApplySelectedProgramAsync, () => _selectedCommand != null && !string.IsNullOrWhiteSpace(_selectedAvailableProgram));
        AddParamFileCommand    = new AsyncRelayCommand(AddParamFileAsync, () => CanRunSshCommand() && _selectedCommand != null && !string.IsNullOrWhiteSpace(_selectedAvailableParamFile));
        CreateParamFileCommand = new AsyncRelayCommand(CreateParamFileAsync, () => CanRunSshCommand() && _selectedCommand != null);
        RemoveParamFileCommand = new RelayCommand<string>(RemoveParamFile);
        EditParamFileCommand   = new AsyncRelayCommand<string>(EditParamFileAsync);
        AddExtraArgCommand     = new RelayCommand(AddExtraArg,    () => _selectedCommand != null);
        RemoveExtraArgCommand  = new RelayCommand<ExtraArgViewModel>(RemoveExtraArg);
        DetectMpiCommand       = new AsyncRelayCommand(DetectMpiAsync, () => CanRunSshCommand() && !string.IsNullOrWhiteSpace(_selectedCommand?.ProgramPath));
        TestCommandCommand     = new AsyncRelayCommand(TestCommandAsync, () => CanRunSshCommand() && !string.IsNullOrWhiteSpace(_selectedCommand?.ProgramPath));
        BrowseProgramPathCommand = new AsyncRelayCommand(BrowseProgramPathAsync, () => CanRunSshCommand() && _selectedCommand != null);
        BrowseParamFileCommand   = new AsyncRelayCommand(BrowseParamFileAsync, () => CanRunSshCommand() && _selectedCommand != null);
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

    private void AddCommand()
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
    }

    private void RemoveCommand(CommandEntryViewModel? cmd)
    {
        if (cmd == null || Commands.Count <= 1) return;
        var idx = Commands.IndexOf(cmd);
        Commands.Remove(cmd);
        SelectedCommand = Commands[Math.Min(idx, Commands.Count - 1)];
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
        var path = await MaterializeParameterFileToWorkDirAsync(_selectedAvailableParamFile, ct);
        if (string.IsNullOrWhiteSpace(path)) return;

        if (!_selectedCommand.ParameterFiles.Contains(path))
            _selectedCommand.ParameterFiles.Add(path);
        _selectedCommand.RebuildCommandLine();
    }

    private async Task ApplySelectedProgramAsync(CancellationToken ct)
    {
        if (_selectedCommand == null || string.IsNullOrWhiteSpace(_selectedAvailableProgram)) return;
        _selectedCommand.ProgramPath = _selectedAvailableProgram.Trim();
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
        var selectedFile = await BrowseRemoteFileAsync(ct, ResolveProgramPickerStartPath(_selectedCommand.ProgramPath));
        if (string.IsNullOrWhiteSpace(selectedFile)) return;
        _selectedCommand.ProgramPath = selectedFile;
        await AutoDetectMpirunForProgramAsync(_selectedCommand, ct, updateStatusWhenMissing: false);
    }

    private async Task BrowseParamFileAsync(CancellationToken ct)
    {
        if (_selectedCommand == null) return;
        var selectedFile = await BrowseRemoteFileAsync(ct, await ResolveHomeDirectoryAsync(ct));
        if (string.IsNullOrWhiteSpace(selectedFile)) return;

        var workCopy = await MaterializeParameterFileToWorkDirAsync(selectedFile, ct);
        if (string.IsNullOrWhiteSpace(workCopy)) return;

        if (!_selectedCommand.ParameterFiles.Contains(workCopy))
            _selectedCommand.ParameterFiles.Add(workCopy);

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
                var overwrite = MessageBox.Show(
                    string.Format(L("CmdBuilder.OverwriteParamPrompt", "工作目录中已存在同名文件：{0}\n是否覆盖？"), remotePath),
                    L("CmdBuilder.OverwriteParamTitle", "覆盖确认"),
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question);
                if (overwrite != MessageBoxResult.OK)
                {
                    StatusMessage = L("CmdBuilder.OperationCancelled", "已取消。");
                    return;
                }
            }

            await _ssh.WriteTextFileAsync(remotePath, string.Empty, ct);
            if (!_selectedCommand.ParameterFiles.Contains(remotePath))
                _selectedCommand.ParameterFiles.Add(remotePath);
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
                var overwrite = MessageBox.Show(
                    string.Format(L("CmdBuilder.OverwriteParamPrompt", "工作目录中已存在同名文件：{0}\n是否覆盖？"), targetPath),
                    L("CmdBuilder.OverwriteParamTitle", "覆盖确认"),
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question);
                if (overwrite != MessageBoxResult.OK)
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
            SetSbatchTimeLimit(initialSbatchOptions.TimeLimit, regenerate: false);
            _sbatchAccount = NormalizeAccount(initialSbatchOptions.Account);
            _sbatchExclusive = initialSbatchOptions.Exclusive;
            return;
        }

        _sbatchNodes = DefaultNodes;
        _sbatchTaskCount = DefaultTaskCount;
        _sbatchCpuCount = string.Empty;
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
            if (!string.IsNullOrWhiteSpace(cmd.ProgramPath))
                cmd.RebuildCommandLine();

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

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("#!/bin/bash -l");
        sb.AppendLine($"#SBATCH --job-name={jobName}");
        sb.AppendLine($"#SBATCH --partition={partition}");
        sb.AppendLine($"#SBATCH --nodes={nodes}");
        sb.AppendLine($"#SBATCH --ntasks={taskCount}");
        if (!string.IsNullOrWhiteSpace(cpuCount))
            sb.AppendLine($"#SBATCH --cpus-per-task={cpuCount}");
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
        return
            "#!/bin/bash -l\n" +
            $"#SBATCH --job-name={jobName}\n" +
            $"#SBATCH --partition={partition}\n" +
            $"#SBATCH --nodes={nodes}\n" +
            $"#SBATCH --ntasks={taskCount}\n" +
            (string.IsNullOrWhiteSpace(cpuCount) ? string.Empty : $"#SBATCH --cpus-per-task={cpuCount}\n") +
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
    }

    private void OnCommandPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CommandEntryViewModel command)
            return;

        if (e.PropertyName is nameof(CommandEntryViewModel.ProgramPath)
            or nameof(CommandEntryViewModel.MpirunPath))
        {
            NotifySelectedCommandValidationChanged(command);
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
        if (string.IsNullOrWhiteSpace(command.ProgramPath))
            issues.Add(L("CmdBuilder.ValidationProgramMissing", "请选择程序路径。"));
        if (command.ParameterFiles.Count == 0)
            issues.Add(L("CmdBuilder.ValidationParamMissing", "请至少添加一个参数文件。"));

        if (issues.Count > 0)
            return string.Join(" ", issues);

        return string.IsNullOrWhiteSpace(command.MpirunPath)
            ? L("CmdBuilder.ValidationReadySerial", "结构完整；当前按非 MPI 命令生成。需要 MPI 时请自动探测或手动填写 mpirun。")
            : L("CmdBuilder.ValidationReadyMpi", "结构完整；已指定 MPI 启动程序。");
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
            if (exitCode != 0)
            {
                if (!string.IsNullOrWhiteSpace(stderr))
                    StatusMessage = string.Format(L("CmdBuilder.QueueLoadFailed", "队列信息加载失败：{0}"), stderr.Trim());
                return;
            }

            var parsed = ParseQueueMetadata(stdout);
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
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(L("CmdBuilder.QueueLoadFailed", "队列信息加载失败：{0}"), ex.Message);
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
            var memoryMb = TryParseLeadingInt(memoryText);
            var cpuCores = TryParseLeadingInt(cpuText);

            if (!map.TryGetValue(name, out var meta))
            {
                meta = new QueueMetadata(name);
                map[name] = meta;
            }

            meta.HasGpu |= hasGpu;
            if (memoryMb.HasValue && (!meta.MemoryMb.HasValue || memoryMb.Value > meta.MemoryMb.Value))
                meta.MemoryMb = memoryMb.Value;
            if (cpuCores.HasValue && (!meta.CpuCores.HasValue || cpuCores.Value > meta.CpuCores.Value))
                meta.CpuCores = cpuCores.Value;
        }

        return map.Values.ToList();
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

    private sealed class QueueMetadata(string name)
    {
        public string Name { get; } = name;
        public bool HasGpu { get; set; }
        public int? MemoryMb { get; set; }
        public int? CpuCores { get; set; }
    }

    private static string L(string key, string fallback)
        => Application.Current?.TryFindResource(key) as string ?? fallback;
}
