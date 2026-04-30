using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using SlurmJobManager.App.ViewModels.Dialogs;
using SlurmJobManager.App.Views;
using SlurmJobManager.App.Views.Dialogs;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Models;
using SlurmJobManager.Core.Services;

namespace SlurmJobManager.App.ViewModels;

/// <summary>
/// Manages task root directory, task ID, parameter templates and sbatch submission.
/// Supports the multi-task-unit workspace model (tasks.manifest.json) while
/// remaining backward-compatible with single-task task.json layouts.
/// </summary>
public sealed class TaskEditorViewModel : ViewModelBase
{
    private readonly ISshClientService _ssh;
    private readonly ISlurmService _slurm;
    private readonly ITaskStorageService _storage;

    // ── Local app-data storage root ──────────────────────────────────────────
    private static readonly string LocalDataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SlurmJobManager", "tasks");

    private static readonly string PinsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SlurmJobManager", "pins.json");

    // ── Remote source directories ────────────────────────────────────────────
    private static readonly string[] AppSourceDirs = { "/env/preprocess/out", "/env/preprocess/bin" };
    private const string RemoteTemplateDir = "/env/preprocess/out/config";

    // ── Scalar backing fields ────────────────────────────────────────────────
    private string _rootDirectory = string.Empty;
    private string _taskId = string.Empty;
    private string? _selectedTemplate;
    private string _templateContent = string.Empty;
    private string _lastSavedTime = string.Empty;
    private string _remoteWorkDir = string.Empty;
    private string _appPath = string.Empty;
    private string _saveAsFileName = string.Empty;
    private string _sbatchTemplate = DefaultSbatchTemplate;
    private bool _isBusy;
    private string _statusMessage = string.Empty;
    private long? _lastJobId;
    private TaskFileEntry? _selectedTaskFile;
    private string _currentTaskFilesPath = string.Empty;

    // ── TaskId directory validation ──────────────────────────────────────────
    private bool? _taskIdDirectoryExists;
    private string _taskIdDirectoryStatus = string.Empty;
    private CancellationTokenSource? _taskIdValidationCts;

    // ── Workspace / active task-unit state ──────────────────────────────────
    // Each TaskId has exactly ONE active task unit.  Legacy workspaces with
    // multiple units are migrated at load time (user picks one; the rest are
    // discarded from the active editing session but remain in the saved file
    // until the next explicit Save).
    private TaskUnitViewModel? _selectedTaskUnit;

    // ── Properties ───────────────────────────────────────────────────────────

    public string RootDirectory
    {
        get => _rootDirectory;
        set
        {
            if (SetField(ref _rootDirectory, value))
            {
                CommandManager.InvalidateRequerySuggested();
                TryAutoFillRemoteWorkDir();
            }
        }
    }

    public string TaskId
    {
        get => _taskId;
        set
        {
            if (SetField(ref _taskId, value))
            {
                CommandManager.InvalidateRequerySuggested();
                TryAutoFillRemoteWorkDir();
                ScheduleTaskIdDirectoryCheck();
            }
        }
    }

    public string? SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (SetField(ref _selectedTemplate, value))
            {
                RebuildFilteredTemplateList();
                if (!string.IsNullOrEmpty(value) && TemplateDisplayList.Contains(value))
                {
                    LoadSelectedRemoteTemplate();
                    SaveAsFileName = value;
                }
            }
        }
    }

    public string TemplateContent   { get => _templateContent;   set => SetField(ref _templateContent, value); }
    public string LastSavedTime     { get => _lastSavedTime;     set => SetField(ref _lastSavedTime, value); }

    public string RemoteWorkDir
    {
        get => _remoteWorkDir;
        set
        {
            if (SetField(ref _remoteWorkDir, value) && string.IsNullOrWhiteSpace(CurrentTaskFilesPath))
            {
                CurrentTaskFilesPath = value;
            }
        }
    }

    public string AppPath
    {
        get => _appPath;
        set { if (SetField(ref _appPath, value)) { RebuildFilteredAppList(); CommandManager.InvalidateRequerySuggested(); } }
    }

    public string SaveAsFileName
    {
        get => _saveAsFileName;
        set => SetField(ref _saveAsFileName, value);
    }

    public string SbatchTemplate    { get => _sbatchTemplate;    set => SetField(ref _sbatchTemplate, value); }
    public bool IsBusy              { get => _isBusy;            set => SetField(ref _isBusy, value); }
    public string StatusMessage     { get => _statusMessage;     set => SetField(ref _statusMessage, value); }
    public long? LastJobId          { get => _lastJobId;         set { SetField(ref _lastJobId, value); OnPropertyChanged(nameof(LastJobIdText)); } }
    public string LastJobIdText     => _lastJobId.HasValue ? $"Last Job ID: {_lastJobId}" : string.Empty;

    public string TaskIdDirectoryStatus
    {
        get => _taskIdDirectoryStatus;
        set => SetField(ref _taskIdDirectoryStatus, value);
    }

    public string CurrentTaskFilesPath
    {
        get => _currentTaskFilesPath;
        set => SetField(ref _currentTaskFilesPath, value);
    }

    public TaskFileEntry? SelectedTaskFile
    {
        get => _selectedTaskFile;
        set => SetField(ref _selectedTaskFile, value);
    }

    // ── Task-unit management ─────────────────────────────────────────────────

    /// <summary>
    /// The single active task unit for the current workspace.
    /// Binding alias exposed to the view.
    /// </summary>
    public TaskUnitViewModel? ActiveUnit => _selectedTaskUnit;

    /// <summary>All task units in the current workspace (exposed for the unit-selector ComboBox).</summary>
    public ObservableCollection<TaskUnitViewModel> TaskUnits { get; } = new();

    /// <summary>Commands for adding / removing task units from the unit selector.</summary>
    public ICommand AddTaskUnitCommand    { get; private set; } = null!;
    public ICommand RemoveTaskUnitCommand { get; private set; } = null!;

    /// <summary>The active (and only) task unit for the current workspace.</summary>
    public TaskUnitViewModel? SelectedTaskUnit
    {
        get => _selectedTaskUnit;
        set
        {
            if (SetField(ref _selectedTaskUnit, value))
            {
                SyncFromSelectedUnit();
                OnPropertyChanged(nameof(ActiveUnit));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    // ── Collections (legacy / shared) ────────────────────────────────────────

    public ObservableCollection<string> AppDisplayList      { get; } = new();
    public ObservableCollection<string> PinnedApps          { get; } = new();
    public ObservableCollection<string> FilteredAppList     { get; } = new();

    public ObservableCollection<string> TemplateDisplayList  { get; } = new();
    public ObservableCollection<string> PinnedTemplates      { get; } = new();
    public ObservableCollection<string> FilteredTemplateList { get; } = new();

    /// <summary>Key/value extra parameters (bound to the active task unit).</summary>
    public ObservableCollection<ParameterEntry> Parameters { get; } = new();

    public ObservableCollection<TaskFileEntry> TaskFiles { get; } = new();

    // ── Commands ─────────────────────────────────────────────────────────────

    public ICommand BrowseRootDirectoryCommand        { get; }
    public ICommand NewTaskIdCommand                  { get; }
    public ICommand SaveTaskCommand                   { get; }
    public ICommand LoadTaskCommand                   { get; }
    public ICommand SaveParamFileCommand              { get; }
    public ICommand SubmitJobCommand                  { get; }
    public ICommand AddParamCommand                   { get; }
    public ICommand RemoveParamCommand                { get; }
    public ICommand RefreshAppCandidatesCommand       { get; }
    public ICommand TogglePinAppCommand               { get; }
    public ICommand RefreshTemplateCandidatesCommand  { get; }
    public ICommand TogglePinTemplateCommand          { get; }
    public ICommand RefreshTaskFilesCommand           { get; }
    public ICommand OpenTaskFileCommand               { get; }
    public ICommand GoUpTaskFilesPathCommand          { get; }

    /// <summary>Opens the Command Builder dialog for the active task unit.</summary>
    public ICommand OpenCommandBuilderCommand { get; }

    // ── Constructor ──────────────────────────────────────────────────────────

    public TaskEditorViewModel(ISshClientService ssh, ISlurmService slurm, ITaskStorageService storage)
    {
        _ssh     = ssh     ?? throw new ArgumentNullException(nameof(ssh));
        _slurm   = slurm   ?? throw new ArgumentNullException(nameof(slurm));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));

        BrowseRootDirectoryCommand       = new AsyncRelayCommand(BrowseRootDirectoryAsync, () => _ssh.IsConnected);
        NewTaskIdCommand                 = new RelayCommand(GenerateNewTaskId, () => _taskIdDirectoryExists != true);
        SaveTaskCommand                  = new AsyncRelayCommand(SaveTaskAsync,     () => !IsBusy);
        LoadTaskCommand                  = new AsyncRelayCommand(LoadTaskAsync,     () => !IsBusy);
        SaveParamFileCommand             = new AsyncRelayCommand(SaveParamFileAsync, () => !IsBusy);
        SubmitJobCommand                 = new AsyncRelayCommand(SubmitJobAsync,    CanSubmit);
        AddParamCommand                  = new RelayCommand(AddParam);
        RemoveParamCommand               = new RelayCommand<ParameterEntry>(p => { if (p != null) Parameters.Remove(p); });
        RefreshAppCandidatesCommand      = new AsyncRelayCommand(RefreshAppCandidatesAsync,       () => _ssh.IsConnected && !IsBusy);
        TogglePinAppCommand              = new RelayCommand(TogglePinApp,                          () => !string.IsNullOrWhiteSpace(AppPath));
        RefreshTemplateCandidatesCommand = new AsyncRelayCommand(RefreshTemplateCandidatesAsync,   () => _ssh.IsConnected && !IsBusy);
        TogglePinTemplateCommand         = new RelayCommand(TogglePinTemplate,                     () => !string.IsNullOrWhiteSpace(SelectedTemplate));
        RefreshTaskFilesCommand          = new AsyncRelayCommand(RefreshTaskFilesAsync,             () => _ssh.IsConnected && !IsBusy);
        OpenTaskFileCommand              = new AsyncRelayCommand<TaskFileEntry>(OpenTaskFileAsync);
        GoUpTaskFilesPathCommand         = new AsyncRelayCommand(GoUpTaskFilesPathAsync,            () => _ssh.IsConnected && !IsBusy);
        OpenCommandBuilderCommand        = new AsyncRelayCommand(OpenCommandBuilderAsync,           () => !IsBusy);
        AddTaskUnitCommand               = new RelayCommand(AddTaskUnitInternal);
        RemoveTaskUnitCommand            = new RelayCommand<TaskUnitViewModel>(RemoveTaskUnit, u => u != null && TaskUnits.Count > 1);

        LoadPins();
        Directory.CreateDirectory(LocalDataRoot);

        // Create a single default task unit so the UI is never empty on first run
        EnsureAtLeastOneTaskUnit();
    }

    // ── Task-unit management ─────────────────────────────────────────────────

    private void EnsureAtLeastOneTaskUnit()
    {
        if (TaskUnits.Count == 0)
        {
            var defaultName = !string.IsNullOrEmpty(TaskId) ? TaskId : "default";
            var unit = new TaskUnitViewModel(new TaskUnit { TaskName = defaultName, Enabled = true });
            TaskUnits.Add(unit);
            SelectedTaskUnit = unit;
        }
    }

    private void AddTaskUnitInternal()
    {
        var name = !string.IsNullOrEmpty(TaskId) ? TaskId : $"Task {TaskUnits.Count + 1}";
        var unit = new TaskUnitViewModel(new TaskUnit { TaskName = name, Enabled = true });
        TaskUnits.Add(unit);
        SelectedTaskUnit = unit;
    }

    private void RemoveTaskUnit(TaskUnitViewModel? unit)
    {
        if (unit == null || TaskUnits.Count <= 1)
        {
            StatusMessage = "至少需要保留一个任务单元。";
            return;
        }
        var idx = TaskUnits.IndexOf(unit);
        TaskUnits.Remove(unit);
        SelectedTaskUnit = TaskUnits[Math.Min(idx, TaskUnits.Count - 1)];
    }

    private void AddProgram()
    {
        _selectedTaskUnit?.Programs.Add(new ProgramEntryViewModel());
    }

    private void RemoveProgram(ProgramEntryViewModel? p)
    {
        if (p != null) _selectedTaskUnit?.Programs.Remove(p);
    }

    private void AddParamFile()
    {
        _selectedTaskUnit?.ParamFiles.Add(new ParameterFileEntryViewModel());
    }

    private void RemoveParamFile(ParameterFileEntryViewModel? f)
    {
        if (f != null) _selectedTaskUnit?.ParamFiles.Remove(f);
    }

    private void AddCommand()
    {
        _selectedTaskUnit?.Commands.Add(new CommandEntryViewModel());
    }

    private void RemoveCommand(CommandEntryViewModel? c)
    {
        if (c != null) _selectedTaskUnit?.Commands.Remove(c);
    }

    private void AddParam()
    {
        // Create independent instances for both collections to avoid shared-reference side-effects
        var editorEntry = new ParameterEntry();
        Parameters.Add(editorEntry);
        if (_selectedTaskUnit != null)
            _selectedTaskUnit.ExtraParams.Add(new ParameterEntry { Key = editorEntry.Key, Value = editorEntry.Value });
    }

    /// <summary>
    /// Sync scalar editor fields (AppPath, RemoteWorkDir, Parameters…) from the
    /// currently selected task unit so the UI reflects that unit's data.
    /// </summary>
    private void SyncFromSelectedUnit()
    {
        if (_selectedTaskUnit == null) return;

        // App path → first program entry
        AppPath = _selectedTaskUnit.Programs.FirstOrDefault()?.ProgramPath ?? string.Empty;

        // Remote work dir
        if (!string.IsNullOrEmpty(_selectedTaskUnit.RemoteWorkDirectory))
        {
            RemoteWorkDir = _selectedTaskUnit.RemoteWorkDirectory;
            CurrentTaskFilesPath = _selectedTaskUnit.RemoteWorkDirectory;
        }

        // Extra parameters
        Parameters.Clear();
        foreach (var ep in _selectedTaskUnit.ExtraParams)
            Parameters.Add(ep);

        // Template selection → first param file
        var firstParam = _selectedTaskUnit.ParamFiles.FirstOrDefault();
        if (firstParam != null)
            SelectedTemplate = firstParam.FilePath;
    }

    /// <summary>
    /// Write scalar editor field changes back into the selected task unit model
    /// before saving, so both representations stay in sync.
    /// </summary>
    private void SyncToSelectedUnit()
    {
        if (_selectedTaskUnit == null) return;

        // App path → first program entry
        if (!string.IsNullOrWhiteSpace(AppPath))
        {
            if (_selectedTaskUnit.Programs.Count == 0)
                _selectedTaskUnit.Programs.Add(new ProgramEntryViewModel());
            _selectedTaskUnit.Programs[0].ProgramPath = AppPath;
        }

        // Remote work dir
        _selectedTaskUnit.RemoteWorkDirectory = RemoteWorkDir;

        // Extra parameters
        _selectedTaskUnit.ExtraParams.Clear();
        foreach (var p in Parameters)
            _selectedTaskUnit.ExtraParams.Add(p);
    }

    // ── B1: Remote root directory + picker ───────────────────────────────────

    public async void OnConnectionEstablished(string username)
    {
        try
        {
            var home = await _ssh.GetHomeDirectoryAsync();
            if (!string.IsNullOrEmpty(home) && string.IsNullOrEmpty(RootDirectory))
                RootDirectory = home;

            await RefreshAppCandidatesAsync(CancellationToken.None);
            await RefreshTemplateCandidatesAsync(CancellationToken.None);
            CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception ex)
        {
            StatusMessage = $"连接后初始化失败：{ex.Message}";
        }
    }

    private async Task BrowseRootDirectoryAsync(CancellationToken ct)
    {
        if (!_ssh.IsConnected)
        {
            StatusMessage = "请先建立 SSH 连接后再浏览远程目录。";
            return;
        }

        string homeDir;
        try { homeDir = await _ssh.GetHomeDirectoryAsync(ct); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TaskEditorViewModel.BrowseRootDirectoryAsync] {ex.Message}");
            homeDir = RootDirectory;
        }

        if (string.IsNullOrEmpty(homeDir)) homeDir = "/home";

        var vm  = new RemoteDirectoryPickerViewModel(_ssh, homeDir);
        var win = new RemoteDirectoryPickerView { DataContext = vm };

        if (Application.Current.MainWindow is { } mainWin) win.Owner = mainWin;
        await vm.LoadInitialAsync(ct);

        if (win.ShowDialog() == true && vm.ResultPath != null)
            RootDirectory = vm.ResultPath;
    }

    private void TryAutoFillRemoteWorkDir()
    {
        if (string.IsNullOrEmpty(RemoteWorkDir)
            && !string.IsNullOrEmpty(RootDirectory)
            && !string.IsNullOrEmpty(TaskId))
        {
            RemoteWorkDir = $"{RootDirectory.TrimEnd('/')}/{TaskId}";
            CurrentTaskFilesPath = RemoteWorkDir;
        }
    }

    private void GenerateNewTaskId()
    {
        TaskId        = $"task_{DateTime.Now:yyyyMMdd_HHmmss}";
        RemoteWorkDir = string.Empty;
        TryAutoFillRemoteWorkDir();
    }

    // ── TaskId directory existence validation ────────────────────────────────

    private void ScheduleTaskIdDirectoryCheck()
    {
        _taskIdValidationCts?.Cancel();
        _taskIdValidationCts?.Dispose();
        _taskIdValidationCts = null;

        _taskIdDirectoryExists = null;
        TaskIdDirectoryStatus  = string.Empty;
        CommandManager.InvalidateRequerySuggested();

        if (!_ssh.IsConnected
            || string.IsNullOrWhiteSpace(RootDirectory)
            || string.IsNullOrWhiteSpace(TaskId))
        {
            return;
        }

        var cts            = new CancellationTokenSource();
        _taskIdValidationCts = cts;
        var capturedTaskId = TaskId;
        var capturedRoot   = RootDirectory;

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(300, cts.Token); }
            catch (OperationCanceledException) { return; }

            if (cts.IsCancellationRequested) return;

            var path = $"{capturedRoot.TrimEnd('/')}/{capturedTaskId}";
            try
            {
                var exists = await _ssh.RemoteDirectoryExistsAsync(path, cts.Token);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    _taskIdDirectoryExists = exists;
                    TaskIdDirectoryStatus  = exists ? "⚠ 目录已存在" : "✓ 目录不存在，可新建";
                    CommandManager.InvalidateRequerySuggested();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    _taskIdDirectoryExists = null;
                    TaskIdDirectoryStatus  = "目录校验失败，请检查连接状态。";
                    System.Diagnostics.Debug.WriteLine($"[TaskIdValidation] {ex.Message}");
                    CommandManager.InvalidateRequerySuggested();
                });
            }
        }, cts.Token);
    }

    // ── App candidates + pinning ─────────────────────────────────────────────

    private async Task RefreshAppCandidatesAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            var all = new List<string>();
            foreach (var dir in AppSourceDirs)
            {
                try
                {
                    var files = await _ssh.ListFilesAsync(dir, ct);
                    all.AddRange(files.Select(f => $"{dir}/{f}"));
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[TaskEditorViewModel] ListFilesAsync({dir}): {ex.Message}"); }
            }
            RebuildAppDisplayList(all);
        }
        catch (Exception ex) { StatusMessage = $"刷新应用路径失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    private void RebuildAppDisplayList(IEnumerable<string>? allCandidates = null)
    {
        var existing = allCandidates ?? AppDisplayList.Where(x => !PinnedApps.Contains(x)).ToList();
        AppDisplayList.Clear();
        foreach (var p in PinnedApps)      AppDisplayList.Add(p);
        foreach (var c in existing.Where(c => !PinnedApps.Contains(c))) AppDisplayList.Add(c);
        RebuildFilteredAppList();
    }

    private void RebuildFilteredAppList()
    {
        var filter = _appPath;
        FilteredAppList.Clear();

        if (string.IsNullOrEmpty(filter) || AppDisplayList.Contains(filter))
        {
            foreach (var item in AppDisplayList) FilteredAppList.Add(item);
            return;
        }

        var lower    = filter.ToLowerInvariant();
        var prefix   = AppDisplayList.Where(x => x.ToLowerInvariant().StartsWith(lower)).OrderBy(x => x);
        var contains = AppDisplayList.Where(x =>  x.ToLowerInvariant().Contains(lower)
                                               && !x.ToLowerInvariant().StartsWith(lower)).OrderBy(x => x);
        foreach (var item in prefix.Concat(contains)) FilteredAppList.Add(item);
    }

    private void RebuildFilteredTemplateList()
    {
        var filter = _selectedTemplate;
        FilteredTemplateList.Clear();

        if (string.IsNullOrEmpty(filter) || TemplateDisplayList.Contains(filter))
        {
            foreach (var item in TemplateDisplayList) FilteredTemplateList.Add(item);
            return;
        }

        var lower    = filter.ToLowerInvariant();
        var prefix   = TemplateDisplayList.Where(x => x.ToLowerInvariant().StartsWith(lower)).OrderBy(x => x);
        var contains = TemplateDisplayList.Where(x =>  x.ToLowerInvariant().Contains(lower)
                                                    && !x.ToLowerInvariant().StartsWith(lower)).OrderBy(x => x);
        foreach (var item in prefix.Concat(contains)) FilteredTemplateList.Add(item);
    }

    private void TogglePinApp()
    {
        if (string.IsNullOrWhiteSpace(AppPath)) return;
        if (PinnedApps.Contains(AppPath)) PinnedApps.Remove(AppPath);
        else PinnedApps.Add(AppPath);
        RebuildAppDisplayList();
        SavePins();
    }

    // ── Template candidates + pinning ────────────────────────────────────────

    private async Task RefreshTemplateCandidatesAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            var files = await _ssh.ListFilesAsync(RemoteTemplateDir, ct);
            RebuildTemplateDisplayList(files);
        }
        catch (Exception ex) { StatusMessage = $"刷新模板列表失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    private void RebuildTemplateDisplayList(IEnumerable<string>? allCandidates = null)
    {
        var existing = allCandidates ?? TemplateDisplayList.Where(x => !PinnedTemplates.Contains(x)).ToList();
        TemplateDisplayList.Clear();
        foreach (var p in PinnedTemplates) TemplateDisplayList.Add(p);
        foreach (var c in existing.Where(c => !PinnedTemplates.Contains(c))) TemplateDisplayList.Add(c);
        RebuildFilteredTemplateList();
    }

    private void TogglePinTemplate()
    {
        if (string.IsNullOrWhiteSpace(SelectedTemplate)) return;
        var t = SelectedTemplate!;
        if (PinnedTemplates.Contains(t)) PinnedTemplates.Remove(t);
        else PinnedTemplates.Add(t);
        RebuildTemplateDisplayList();
        SavePins();
    }

    private void LoadSelectedRemoteTemplate()
    {
        if (string.IsNullOrEmpty(_selectedTemplate)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var remotePath = $"{RemoteTemplateDir}/{_selectedTemplate}";
                var content    = await _ssh.ReadTextFileAsync(remotePath);
                Application.Current.Dispatcher.Invoke(() => TemplateContent = content);
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                    StatusMessage = $"加载模板失败：{ex.Message}");
            }
        });
    }

    // ── Pin persistence ──────────────────────────────────────────────────────

    private sealed record PinsData(List<string> PinnedApps, List<string> PinnedTemplates);

    private void LoadPins()
    {
        try
        {
            if (!File.Exists(PinsFilePath)) return;
            var json = File.ReadAllText(PinsFilePath);
            var data = JsonSerializer.Deserialize<PinsData>(json);
            if (data is null) return;
            foreach (var a in data.PinnedApps)      PinnedApps.Add(a);
            foreach (var t in data.PinnedTemplates) PinnedTemplates.Add(t);
            RebuildAppDisplayList();
            RebuildTemplateDisplayList();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[TaskEditorViewModel.LoadPins] {ex.Message}"); }
    }

    private void SavePins()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PinsFilePath)!);
            var data = new PinsData(PinnedApps.ToList(), PinnedTemplates.ToList());
            File.WriteAllText(PinsFilePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[TaskEditorViewModel.SavePins] {ex.Message}"); }
    }

    // ── Parameter file save ───────────────────────────────────────────────────

    private async Task SaveParamFileAsync(CancellationToken ct)
    {
        if (!ValidateRootAndId() || string.IsNullOrWhiteSpace(SaveAsFileName))
        {
            StatusMessage = "请先完成必填项后再保存参数文件（根目录、TaskId 和文件名不可为空）。";
            return;
        }

        if (!_ssh.IsConnected)
        {
            StatusMessage = "请先建立 SSH 连接后再保存参数文件。";
            return;
        }

        IsBusy = true;
        try
        {
            var remoteParamsDir = $"{RemoteWorkDir.TrimEnd('/')}/params";
            await _ssh.ExecuteAsync($"mkdir -p {EscapeShellArg(remoteParamsDir)}", ct);

            var remoteDest = $"{remoteParamsDir}/{SaveAsFileName}";

            if (await _ssh.RemoteFileExistsAsync(remoteDest, ct))
            {
                var msgText  = Application.Current?.TryFindResource("Task.OverwritePrompt") as string
                               ?? $"远程文件 {remoteDest} 已存在，是否覆盖？";
                var msgTitle = Application.Current?.TryFindResource("Task.OverwriteTitle") as string ?? "覆盖确认";
                var result   = MessageBox.Show(
                    string.Format(msgText, remoteDest), msgTitle,
                    MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (result != MessageBoxResult.OK)
                {
                    StatusMessage = Application.Current?.TryFindResource("Task.SaveCancelled") as string ?? "保存已取消。";
                    return;
                }
            }

            await _ssh.WriteTextFileAsync(remoteDest, TemplateContent, ct);
            LastSavedTime = $"已保存：{DateTime.Now:HH:mm:ss}";
            StatusMessage = $"参数文件已保存：{remoteDest}";

            // Register the saved file in the selected unit's param file list
            if (_selectedTaskUnit != null &&
                _selectedTaskUnit.ParamFiles.All(f => f.FilePath != remoteDest))
            {
                _selectedTaskUnit.ParamFiles.Add(
                    new ParameterFileEntryViewModel(new ParameterFileEntry { FilePath = remoteDest, Alias = SaveAsFileName }));
            }
        }
        catch (Exception ex) { StatusMessage = $"保存参数文件失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    // ── Task directory file list ──────────────────────────────────────────────

    private async Task RefreshTaskFilesAsync(CancellationToken ct)
    {
        if (!_ssh.IsConnected)
        {
            StatusMessage = "请先建立 SSH 连接。";
            return;
        }

        var targetPath = NormalizeRemotePath(CurrentTaskFilesPath);
        if (string.IsNullOrWhiteSpace(targetPath))
            targetPath = NormalizeRemotePath(RemoteWorkDir);
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            StatusMessage = "请先设置任务目录或输入浏览路径。";
            return;
        }

        IsBusy = true;
        try
        {
            var escapedPath = EscapeShellArg(targetPath);
            var (kindStdOut, _, kindExitCode) = await _ssh.ExecuteAsync(
                $"if [ -d {escapedPath} ]; then echo DIR; " +
                $"elif [ -f {escapedPath} ]; then echo FILE; else echo MISSING; fi", ct);

            var kind = kindStdOut.Trim();
            if (kindExitCode != 0 || kind == "MISSING")
            {
                StatusMessage = "路径不存在：请检查输入目录。";
                return;
            }
            if (kind == "FILE")
            {
                StatusMessage = "该路径是文件而非目录，请输入目录路径。";
                return;
            }

            var (stdout, stderr, exitCode) = await _ssh.ExecuteAsync(
                $"ls -1Ap {escapedPath}", ct);

            TaskFiles.Clear();
            CurrentTaskFilesPath = targetPath;

            if (exitCode != 0)
            {
                var errDetail = stderr.Trim();
                StatusMessage = string.IsNullOrEmpty(errDetail)
                    ? "无法读取目录，请检查权限。"
                    : $"无法读取目录：{errDetail}";
                return;
            }

            var entries = stdout.Split('\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var entry in entries)
            {
                var isDirectory = entry.EndsWith("/", StringComparison.Ordinal);
                var name = isDirectory ? entry[..^1] : entry;
                if (name == "." || name == "..") continue;
                TaskFiles.Add(new TaskFileEntry(name, isDirectory, BuildRemotePath(targetPath, name)));
            }

            StatusMessage = TaskFiles.Count == 0 ? "任务目录为空。" : $"已加载 {TaskFiles.Count} 个条目。";
        }
        catch (Exception ex) { StatusMessage = $"刷新文件列表失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    private async Task GoUpTaskFilesPathAsync(CancellationToken ct)
    {
        var current = NormalizeRemotePath(CurrentTaskFilesPath);
        if (string.IsNullOrWhiteSpace(current))
        {
            StatusMessage = "当前路径为空，无法返回上级。";
            return;
        }

        var up = current == "/" ? "/" : current[..current.LastIndexOf('/')];
        CurrentTaskFilesPath = string.IsNullOrWhiteSpace(up) ? "/" : up;
        await RefreshTaskFilesAsync(ct);
    }

    private async Task OpenTaskFileAsync(TaskFileEntry? fileEntry, CancellationToken ct)
    {
        if (fileEntry == null) return;
        if (!_ssh.IsConnected) { StatusMessage = "请先建立 SSH 连接。"; return; }

        if (fileEntry.IsDirectory)
        {
            CurrentTaskFilesPath = fileEntry.RemotePath;
            await RefreshTaskFilesAsync(ct);
            return;
        }

        var remotePath = fileEntry.RemotePath;
        if (await IsLikelyBinaryFileAsync(remotePath, ct))
        {
            StatusMessage = $"文件 {fileEntry.Name} 可能是二进制文件，已阻止打开。";
            return;
        }

        var vm  = new RemoteFileEditorViewModel(_ssh, remotePath);
        var win = new RemoteFileEditorView { DataContext = vm };

        if (Application.Current.MainWindow is { } mainWin) win.Owner = mainWin;
        await vm.LoadAsync(ct);
        if (vm.IsBinaryFile)
        {
            StatusMessage = vm.StatusMessage;
            return;
        }

        win.ShowDialog();
    }

    // ── Command Builder dialog ────────────────────────────────────────────────

    private async Task OpenCommandBuilderAsync(CancellationToken ct)
    {
        EnsureAtLeastOneTaskUnit();
        var unit = _selectedTaskUnit!;

        var dlgVm = new CommandBuilderViewModel(
            _ssh,
            taskId:         TaskId,
            remoteWorkDir:  RemoteWorkDir,
            initialCommands: unit.Commands.Select(c => c.ToModel()),
            initialSbatch:  unit.SbatchTemplate);

        var win = new CommandBuilderView { DataContext = dlgVm };
        if (Application.Current.MainWindow is { } mainWin) win.Owner = mainWin;

        if (_ssh.IsConnected)
            await dlgVm.LoadInitialAsync(ct);

        if (win.ShowDialog() == true && dlgVm.Confirmed)
        {
            // Apply commands back to the active task unit
            unit.Commands.Clear();
            foreach (var ce in dlgVm.GetResultCommands())
                unit.Commands.Add(new CommandEntryViewModel(ce));

            // Sync first command's program path back into Programs list for legacy submit logic
            var firstProg = dlgVm.GetResultCommands()
                .Where(c => !string.IsNullOrWhiteSpace(c.ProgramPath))
                .FirstOrDefault()?.ProgramPath ?? string.Empty;
            unit.Programs.Clear();
            if (!string.IsNullOrWhiteSpace(firstProg))
                unit.Programs.Add(new ProgramEntryViewModel(new Core.Models.ProgramEntry { ProgramPath = firstProg, Order = 0 }));
            AppPath = firstProg;

            // Persist the user-edited sbatch content on the unit
            unit.SbatchTemplate = dlgVm.GetResultSbatch();

            StatusMessage = Application.Current?.TryFindResource("Task.CommandUpdated") as string ?? "命令已更新，请记得保存任务。";
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // ── Persistence (workspace + legacy) ────────────────────────────────────

    private string GetLocalTaskDir() => Path.Combine(LocalDataRoot, TaskId);

    private async Task SaveTaskAsync(CancellationToken ct)
    {
        if (!ValidateRootAndId()) return;
        IsBusy = true;
        StatusMessage = "保存任务…";
        try
        {
            Directory.CreateDirectory(GetLocalTaskDir());
            SyncToSelectedUnit();

            var workspace = BuildWorkspace();
            await _storage.SaveWorkspaceAsync(workspace, ct);

            // Also persist legacy task.json for tooling that reads only that
            await _storage.SaveAsync(BuildTaskRecord(), ct);

            StatusMessage = $"任务已保存：{GetLocalTaskDir()}";
        }
        catch (Exception ex) { StatusMessage = $"保存失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    private async Task LoadTaskAsync(CancellationToken ct)
    {
        if (!ValidateRootAndId()) return;
        IsBusy = true;
        StatusMessage = "加载任务…";
        try
        {
            var workspace = await _storage.LoadWorkspaceAsync(LocalDataRoot, TaskId, ct);
            if (workspace == null)
            {
                // Try legacy path
                var record = await _storage.LoadAsync(LocalDataRoot, TaskId, ct);
                if (record == null) { StatusMessage = "未找到任务数据（task.json / tasks.manifest.json）。"; return; }
                ApplyTaskRecord(record);
                StatusMessage = $"任务已加载（旧格式）：{TaskId}";
                return;
            }

            ApplyWorkspace(workspace);
            StatusMessage = $"任务工作区已加载：{TaskId}（{workspace.Tasks.Count} 个任务单元）";
        }
        catch (Exception ex) { StatusMessage = $"加载失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    private void ApplyWorkspace(TaskWorkspace w)
    {
        TaskUnits.Clear();

        if (w.Tasks.Count == 0)
        {
            // No units in workspace — create a fresh default
            EnsureAtLeastOneTaskUnit();
            return;
        }

        if (w.Tasks.Count == 1)
        {
            // Exactly one unit — the happy path
            TaskUnits.Add(new TaskUnitViewModel(w.Tasks[0]));
            SelectedTaskUnit = TaskUnits[0];
            return;
        }

        // Legacy multi-unit workspace: prompt the user to pick one active unit.
        // All units are loaded internally but only the chosen one is used for editing/submitting.
        foreach (var unit in w.Tasks)
            TaskUnits.Add(new TaskUnitViewModel(unit));

        var names   = TaskUnits.Select((u, i) => $"{i + 1}. {u.TaskName}").ToArray();
        var prompt  = Application.Current?.TryFindResource("Task.MultiUnitPrompt") as string
                      ?? "检测到多个任务单元（旧数据）。本次将仅使用第一个单元作为活动单元。\n\n单元列表：\n{0}\n\n提交时只提交活动单元。";
        StatusMessage = string.Format(prompt, string.Join("\n", names));

        // Default: use the first unit
        SelectedTaskUnit = TaskUnits[0];
    }

    private TaskWorkspace BuildWorkspace() => new()
    {
        TaskId   = TaskId,
        RootPath = LocalDataRoot,
        Tasks    = TaskUnits.Select(u => u.ToModel()).ToList(),
    };

    // ── sbatch submit ────────────────────────────────────────────────────────

    private async Task SubmitJobAsync(CancellationToken ct)
    {
        if (!ValidateSubmitRequirements()) return;

        if (_selectedTaskUnit == null)
        {
            StatusMessage = "没有活动任务单元可提交，请先保存任务配置。";
            return;
        }

        IsBusy = true;
        StatusMessage = "准备提交…";
        try
        {
            var jobId = await SubmitUnitAsync(_selectedTaskUnit, ct);
            LastJobId = jobId;
            StatusMessage = $"作业已提交！Job ID = {jobId}";
        }
        catch (Exception ex) { StatusMessage = $"提交失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    private async Task<long> SubmitUnitAsync(TaskUnitViewModel unit, CancellationToken ct)
    {
        var workDir = !string.IsNullOrEmpty(unit.RemoteWorkDirectory)
            ? unit.RemoteWorkDirectory
            : RemoteWorkDir;

        var appPath = unit.Programs.FirstOrDefault()?.ProgramPath ?? AppPath;

        var paramFile = GetParameterFilePath(unit, workDir);

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["JOB_NAME"]    = !string.IsNullOrEmpty(unit.TaskName) ? unit.TaskName : TaskId,
            ["WORK_DIR"]    = workDir,
            ["APP_PATH"]    = appPath,
            ["PARAM_FILE"]  = paramFile,
            ["STDOUT_FILE"] = $"{workDir}/logs/job.out",
            ["STDERR_FILE"] = $"{workDir}/logs/job.err",
        };
        foreach (var (k, v) in unit.ToModel().ExtraParameters)
            parameters[k] = v;

        var template  = !string.IsNullOrEmpty(unit.SbatchTemplate) ? unit.SbatchTemplate : SbatchTemplate;
        var rendered  = new SbatchTemplateRenderer(template).Render(parameters);

        var localTaskDir = GetLocalTaskDir();
        var scriptsDir   = Path.Combine(localTaskDir, unit.TaskName, "scripts");
        Directory.CreateDirectory(scriptsDir);
        Directory.CreateDirectory(Path.Combine(localTaskDir, unit.TaskName, "logs"));

        var localScript = Path.Combine(scriptsDir, "submit.sbatch");
        await File.WriteAllTextAsync(localScript, rendered, ct);

        StatusMessage = $"提交 {unit.TaskName}…";
        var jobId = await _slurm.SubmitSbatchAsync(localScript, workDir, ct);

        unit.SlurmJobId = jobId;

        await File.WriteAllTextAsync(
            Path.Combine(localTaskDir, unit.TaskName, "logs", "submit.log"),
            $"Submitted at {DateTime.UtcNow:u}\nJob ID: {jobId}\n",
            ct);

        return jobId;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the remote parameter file path for a task unit submission.
    /// Priority: unit's first ParameterFile → global SelectedTemplate → empty.
    /// </summary>
    private string GetParameterFilePath(TaskUnitViewModel unit, string workDir)
    {
        var firstParam = unit.ParamFiles.FirstOrDefault()?.FilePath;
        if (!string.IsNullOrEmpty(firstParam))
            return $"{workDir.TrimEnd('/')}/params/{firstParam}".Replace('\\', '/');

        if (!string.IsNullOrEmpty(SelectedTemplate))
            return $"{workDir.TrimEnd('/')}/params/{SelectedTemplate}".Replace('\\', '/');

        return string.Empty;
    }

    private bool CanSubmit() =>
        !IsBusy
        && _ssh.IsConnected
        && !string.IsNullOrWhiteSpace(RootDirectory)
        && !string.IsNullOrWhiteSpace(TaskId)
        && (!string.IsNullOrWhiteSpace(AppPath)
            || (_selectedTaskUnit?.Programs.Any(p => !string.IsNullOrWhiteSpace(p.ProgramPath)) == true));

    private bool ValidateRootAndId()
    {
        if (string.IsNullOrWhiteSpace(RootDirectory)) { StatusMessage = "请先配置任务根目录"; return false; }
        if (string.IsNullOrWhiteSpace(TaskId))         { StatusMessage = "请先填写 TaskId";    return false; }
        return true;
    }

    private bool ValidateSubmitRequirements()
    {
        if (!ValidateRootAndId()) return false;
        var hasApp = !string.IsNullOrWhiteSpace(AppPath)
            || (_selectedTaskUnit?.Programs.Any(p => !string.IsNullOrWhiteSpace(p.ProgramPath)) == true);
        if (!hasApp) { StatusMessage = "请先选择应用程序路径"; return false; }
        return true;
    }

    private TaskRecord BuildTaskRecord() => new()
    {
        TaskId              = TaskId,
        LocalRootDirectory  = LocalDataRoot,
        RemoteWorkDirectory = RemoteWorkDir,
        TemplateFileName    = SelectedTemplate,
        SlurmJobId          = LastJobId,
        Parameters          = Parameters
            .Where(p => !string.IsNullOrWhiteSpace(p.Key))
            .ToDictionary(p => p.Key, p => p.Value),
    };

    private void ApplyTaskRecord(TaskRecord r)
    {
        RemoteWorkDir    = r.RemoteWorkDirectory;
        CurrentTaskFilesPath = r.RemoteWorkDirectory;
        LastJobId        = r.SlurmJobId;
        SelectedTemplate = r.TemplateFileName;
        Parameters.Clear();
        foreach (var (k, v) in r.Parameters)
            Parameters.Add(new ParameterEntry { Key = k, Value = v });
    }

    private static string NormalizeRemotePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var normalized = path.Trim();
        while (normalized.Contains("//", StringComparison.Ordinal))
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        if (normalized.Length > 1 && normalized.EndsWith("/", StringComparison.Ordinal))
            normalized = normalized[..^1];
        return normalized;
    }

    private static string BuildRemotePath(string currentPath, string itemName)
    {
        var prefix = NormalizeRemotePath(currentPath);
        if (prefix == "/") return $"/{itemName}";
        return $"{prefix}/{itemName}";
    }

    private async Task<bool> IsLikelyBinaryFileAsync(string remotePath, CancellationToken ct)
    {
        var bytes = await _ssh.ReadFileBytesAsync(remotePath, ct);
        var sampleLength = Math.Min(bytes.Length, 4096);
        if (sampleLength == 0) return false;

        var suspicious = 0;
        for (var i = 0; i < sampleLength; i++)
        {
            var b = bytes[i];
            if (b == 0) return true;
            if (b < 0x09 || (b > 0x0D && b < 0x20)) suspicious++;
        }

        return suspicious > sampleLength / 8;
    }

    private static string EscapeShellArg(string arg)
        => "'" + arg.Replace("'", "'\\''") + "'";

    // ── Default sbatch template ──────────────────────────────────────────────

    private const string DefaultSbatchTemplate =
        "#!/bin/bash\n" +
        "#SBATCH --job-name={{JOB_NAME}}\n" +
        "#SBATCH --output={{STDOUT_FILE}}\n" +
        "#SBATCH --error={{STDERR_FILE}}\n" +
        "#SBATCH --chdir={{WORK_DIR}}\n" +
        "#SBATCH --ntasks=1\n" +
        "#SBATCH --cpus-per-task=1\n" +
        "#SBATCH --time=01:00:00\n" +
        "\n" +
        "echo \"Starting job {{JOB_NAME}} at $(date)\"\n" +
        "{{APP_PATH}} {{PARAM_FILE}}\n" +
        "echo \"Job finished at $(date)\"\n";
}

/// <summary>A single editable parameter key/value pair.</summary>
public sealed class ParameterEntry : ViewModelBase
{
    private string _key = string.Empty;
    private string _value = string.Empty;
    public string Key   { get => _key;   set => SetField(ref _key, value); }
    public string Value { get => _value; set => SetField(ref _value, value); }
}

public sealed class TaskFileEntry
{
    public TaskFileEntry(string name, bool isDirectory, string remotePath)
    {
        Name = name;
        IsDirectory = isDirectory;
        RemotePath = remotePath;
    }

    public string Name { get; }
    public bool IsDirectory { get; }
    public string RemotePath { get; }
    public string DisplayName => IsDirectory ? $"{Name}/" : Name;
}
