using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using SlurmJobManager.App.ViewModels;
using SlurmJobManager.App.Views;
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
    private const string RemoteParamDir = "/env/preprocess/out/config";

    private readonly ISshClientService _ssh;

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

    // ── Result ─────────────────────────────────────────────────────────────
    public bool Confirmed { get; private set; }

    // ── Available remote file lists ────────────────────────────────────────
    public ObservableCollection<string> AllPrograms      { get; } = new();
    public ObservableCollection<string> FilteredPrograms { get; } = new();
    public ObservableCollection<string> AllParamFiles    { get; } = new();
    public ObservableCollection<string> FilteredParamFiles { get; } = new();

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
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool HasSelectedCommand => _selectedCommand != null;

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

    // ── Commands ───────────────────────────────────────────────────────────
    public ICommand RefreshCommand          { get; }
    public ICommand AddCommandCommand       { get; }
    public ICommand RemoveCommandCommand    { get; }
    public ICommand MoveUpCommand           { get; }
    public ICommand MoveDownCommand         { get; }
    public ICommand ApplySelectedProgramCommand { get; }
    public ICommand AddParamFileCommand     { get; }
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
        string taskId = "",
        string remoteWorkDir = "",
        IEnumerable<CommandEntry>? initialCommands = null,
        string? initialSbatch = null)
    {
        _ssh           = ssh           ?? throw new ArgumentNullException(nameof(ssh));
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
        ApplySelectedProgramCommand = new RelayCommand(ApplySelectedProgram, () => _selectedCommand != null && !string.IsNullOrWhiteSpace(_selectedAvailableProgram));
        AddParamFileCommand    = new RelayCommand(AddParamFile,    () => _selectedCommand != null && !string.IsNullOrWhiteSpace(_selectedAvailableParamFile));
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
        var cmd = new CommandEntryViewModel { Order = Commands.Count };
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

    private void AddParamFile()
    {
        if (_selectedCommand == null || string.IsNullOrWhiteSpace(_selectedAvailableParamFile)) return;
        var path = _selectedAvailableParamFile;
        if (!_selectedCommand.ParameterFiles.Contains(path))
            _selectedCommand.ParameterFiles.Add(path);
        _selectedCommand.RebuildCommandLine();
    }

    private void ApplySelectedProgram()
    {
        if (_selectedCommand == null || string.IsNullOrWhiteSpace(_selectedAvailableProgram)) return;
        _selectedCommand.ProgramPath = _selectedAvailableProgram.Trim();
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

        var vm  = new RemoteFileEditorViewModel(_ssh, path);
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

        IsBusy = true;
        StatusMessage = $"正在通过 ldd 分析 MPI 依赖…";
        try
        {
            var mpirun = await InferMpirunPathAsync(_selectedCommand.ProgramPath, ct);
            if (!string.IsNullOrEmpty(mpirun))
            {
                _selectedCommand.MpirunPath = mpirun;
                _selectedCommand.RebuildCommandLine();
                StatusMessage = $"已推断 mpirun 路径：{mpirun}";
                RegenerateSbatch();
            }
            else
            {
                StatusMessage = "未能自动推断 mpirun 路径，请手动填写。";
            }
        }
        catch (Exception ex) { StatusMessage = $"MPI 路径推断失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Runs <c>ldd &lt;program&gt;</c> on the remote, looks for libmpi.so,
    /// derives the OpenMPI prefix and checks that <prefix>/bin/mpirun exists.
    /// Falls back to <c>which mpirun</c> on failure.
    /// </summary>
    private async Task<string> InferMpirunPathAsync(string programPath, CancellationToken ct)
    {
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

        // Step 2: which mpirun
        var (whichOut, _, whichExit) = await _ssh.ExecuteAsync("which mpirun 2>/dev/null", ct);
        if (whichExit == 0 && !string.IsNullOrWhiteSpace(whichOut))
        {
            var path = whichOut.Trim();
            if (!string.IsNullOrEmpty(path)) return path;
        }

        return string.Empty;
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
        var selectedFile = await BrowseRemoteFileAsync(ct);
        if (string.IsNullOrWhiteSpace(selectedFile)) return;
        _selectedCommand.ProgramPath = selectedFile;
    }

    private async Task BrowseParamFileAsync(CancellationToken ct)
    {
        if (_selectedCommand == null) return;
        var selectedFile = await BrowseRemoteFileAsync(ct);
        if (string.IsNullOrWhiteSpace(selectedFile)) return;

        if (!_selectedCommand.ParameterFiles.Contains(selectedFile))
            _selectedCommand.ParameterFiles.Add(selectedFile);

        SelectedAvailableParamFile = selectedFile;
        _selectedCommand.RebuildCommandLine();
    }

    private async Task<string?> BrowseRemoteFileAsync(CancellationToken ct)
    {
        if (!IsSshConnectedSafe())
        {
            StatusMessage = L("Task.RequireConnectionForBrowse", "请先建立 SSH 连接后再浏览远程目录。");
            return null;
        }

        var homeDir = await ResolveHomeDirectoryAsync(ct);
        var vm = new RemoteFilePickerViewModel(_ssh, homeDir);
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

    // ── sbatch generation ──────────────────────────────────────────────────

    /// <summary>Rebuilds the sbatch content from the current commands list.</summary>
    public void RegenerateSbatch()
    {
        var firstProg = Commands.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.ProgramPath))?.ProgramPath ?? string.Empty;
        var progName  = GetFileNameFromPath(firstProg);

        var jobName = string.IsNullOrEmpty(_taskId)
            ? progName
            : (string.IsNullOrEmpty(progName) ? _taskId : $"{_taskId}{progName}");

        var workDir = string.IsNullOrEmpty(_remoteWorkDir) ? "/tmp/job" : _remoteWorkDir;
        var stdout  = $"{workDir}/logs/job.out";
        var stderr  = $"{workDir}/logs/job.err";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("#!/bin/bash -l");
        sb.AppendLine($"#SBATCH --job-name={jobName}");
        sb.AppendLine("#SBATCH --partition=");
        sb.AppendLine("#SBATCH --nodes=1");
        sb.AppendLine("#SBATCH --ntasks=1");
        sb.AppendLine("#SBATCH --cpus-per-task=1");
        sb.AppendLine("#SBATCH --time=01:00:00");
        sb.AppendLine($"#SBATCH --output={stdout}");
        sb.AppendLine($"#SBATCH --error={stderr}");
        sb.AppendLine($"#SBATCH --chdir={workDir}");
        sb.AppendLine();
        sb.AppendLine("module purge");
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
        var jobName = string.IsNullOrEmpty(_taskId) ? "job" : _taskId;
        return
            "#!/bin/bash -l\n" +
            $"#SBATCH --job-name={jobName}\n" +
            "#SBATCH --partition=\n" +
            "#SBATCH --nodes=1\n" +
            "#SBATCH --ntasks=1\n" +
            "#SBATCH --cpus-per-task=1\n" +
            "#SBATCH --time=01:00:00\n" +
            $"#SBATCH --output={workDir}/logs/job.out\n" +
            $"#SBATCH --error={workDir}/logs/job.err\n" +
            $"#SBATCH --chdir={workDir}\n\n" +
            "module purge\n\n" +
            $"echo \"Starting job {jobName} at $(date)\"\n\n" +
            "# commands here\n\n" +
            "echo \"Job finished at $(date)\"\n";
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private void WireCommandCollections(CommandEntryViewModel cmd)
    {
        cmd.ParameterFiles.CollectionChanged += (_, __) => cmd.RebuildCommandLine();
        cmd.ExtraArgs.CollectionChanged      += (_, __) => cmd.RebuildCommandLine();
    }

    private static string EscapeShell(string arg)
        => "'" + arg.Replace("'", "'\\''") + "'";

    private static string TruncateText(string text, int maxLen)
        => text.Length <= maxLen ? text : text[..maxLen] + "…";

    /// <summary>Safely extracts the file name component from a Unix path.</summary>
    private static string GetFileNameFromPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var slashIdx = path.LastIndexOf('/');
        return slashIdx >= 0 ? path[(slashIdx + 1)..] : path;
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

    private static string L(string key, string fallback)
        => Application.Current?.TryFindResource(key) as string ?? fallback;
}
