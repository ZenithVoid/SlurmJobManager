using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using SlurmJobManager.App.Views;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Models;
using SlurmJobManager.Core.Services;

namespace SlurmJobManager.App.ViewModels;

/// <summary>
/// Manages task root directory, task ID, parameter templates and sbatch submission.
/// </summary>
public sealed class TaskEditorViewModel : ViewModelBase
{
    private readonly ISshClientService _ssh;
    private readonly ISlurmService _slurm;
    private readonly ITaskStorageService _storage;

    // ── Local app-data storage root (task.json, local scripts) ──────────────
    private static readonly string LocalDataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SlurmJobManager", "tasks");

    private static readonly string PinsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SlurmJobManager", "pins.json");

    // ── Remote source directories for app path and templates ────────────────
    private static readonly string[] AppSourceDirs = { "/env/preprocess/out", "/env/preprocess/bin" };
    private const string RemoteTemplateDir = "/env/preprocess/out/config";

    // ── Backing fields ───────────────────────────────────────────────────────
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
    private string? _selectedTaskFile;

    // ── TaskId directory validation ──────────────────────────────────────────
    private bool? _taskIdDirectoryExists;          // null=unknown, true=exists, false=not exists
    private string _taskIdDirectoryStatus = string.Empty;
    private CancellationTokenSource? _taskIdValidationCts;

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
                // Only load template content and update save-as filename when an exact match is selected
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
        set => SetField(ref _remoteWorkDir, value);
    }

    public string AppPath
    {
        get => _appPath;
        set { if (SetField(ref _appPath, value)) { RebuildFilteredAppList(); CommandManager.InvalidateRequerySuggested(); } }
    }

    /// <summary>Filename used for Save As; defaults to SelectedTemplate when a template is chosen.</summary>
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

    /// <summary>Validation message shown below the TaskId input (e.g. "目录已存在" or "目录不存在，可新建").</summary>
    public string TaskIdDirectoryStatus
    {
        get => _taskIdDirectoryStatus;
        set => SetField(ref _taskIdDirectoryStatus, value);
    }

    public string? SelectedTaskFile
    {
        get => _selectedTaskFile;
        set => SetField(ref _selectedTaskFile, value);
    }

    // ── Collections ──────────────────────────────────────────────────────────

    /// <summary>Remote app executable candidates (pinned first, then the rest).</summary>
    public ObservableCollection<string> AppDisplayList     { get; } = new();
    /// <summary>Pinned app paths.</summary>
    public ObservableCollection<string> PinnedApps         { get; } = new();

    /// <summary>App path candidates filtered by the current <see cref="AppPath"/> text input.</summary>
    public ObservableCollection<string> FilteredAppList    { get; } = new();

    /// <summary>Remote template file candidates (pinned first, then the rest).</summary>
    public ObservableCollection<string> TemplateDisplayList { get; } = new();
    /// <summary>Pinned template filenames.</summary>
    public ObservableCollection<string> PinnedTemplates    { get; } = new();

    /// <summary>Template candidates filtered by the current <see cref="SelectedTemplate"/> text input.</summary>
    public ObservableCollection<string> FilteredTemplateList { get; } = new();

    /// <summary>Key/value extra parameters for sbatch.</summary>
    public ObservableCollection<ParameterEntry> Parameters { get; } = new();

    /// <summary>Files listed under the current task's remote work directory.</summary>
    public ObservableCollection<string> TaskFiles { get; } = new();

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

    // ── Constructor ──────────────────────────────────────────────────────────

    public TaskEditorViewModel(ISshClientService ssh, ISlurmService slurm, ITaskStorageService storage)
    {
        _ssh     = ssh     ?? throw new ArgumentNullException(nameof(ssh));
        _slurm   = slurm   ?? throw new ArgumentNullException(nameof(slurm));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));

        BrowseRootDirectoryCommand       = new AsyncRelayCommand(BrowseRootDirectoryAsync, () => _ssh.IsConnected);
        // "新建" is disabled when the target directory already exists, preventing accidental overwrites
        NewTaskIdCommand                 = new RelayCommand(GenerateNewTaskId, () => _taskIdDirectoryExists != true);
        SaveTaskCommand                  = new AsyncRelayCommand(SaveTaskAsync,     () => !IsBusy);
        LoadTaskCommand                  = new AsyncRelayCommand(LoadTaskAsync,     () => !IsBusy);
        SaveParamFileCommand             = new AsyncRelayCommand(SaveParamFileAsync, () => !IsBusy);
        SubmitJobCommand                 = new AsyncRelayCommand(SubmitJobAsync,    CanSubmit);
        AddParamCommand                  = new RelayCommand(() => Parameters.Add(new ParameterEntry()));
        RemoveParamCommand               = new RelayCommand<ParameterEntry>(p => { if (p != null) Parameters.Remove(p); });
        RefreshAppCandidatesCommand      = new AsyncRelayCommand(RefreshAppCandidatesAsync,       () => _ssh.IsConnected && !IsBusy);
        TogglePinAppCommand              = new RelayCommand(TogglePinApp,                          () => !string.IsNullOrWhiteSpace(AppPath));
        RefreshTemplateCandidatesCommand = new AsyncRelayCommand(RefreshTemplateCandidatesAsync,   () => _ssh.IsConnected && !IsBusy);
        TogglePinTemplateCommand         = new RelayCommand(TogglePinTemplate,                     () => !string.IsNullOrWhiteSpace(SelectedTemplate));
        RefreshTaskFilesCommand          = new AsyncRelayCommand(RefreshTaskFilesAsync,             () => _ssh.IsConnected && !IsBusy);
        OpenTaskFileCommand              = new AsyncRelayCommand<string>(OpenTaskFileAsync);

        LoadPins();
        Directory.CreateDirectory(LocalDataRoot);
    }

    // ── B1: Remote root directory auto-fill + picker ─────────────────────────

    /// <summary>Called by ConnectionViewModel via MainViewModel when SSH connects successfully.</summary>
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
        try
        {
            homeDir = await _ssh.GetHomeDirectoryAsync(ct);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TaskEditorViewModel.BrowseRootDirectoryAsync] GetHomeDirectory: {ex.Message}");
            homeDir = RootDirectory;
        }

        if (string.IsNullOrEmpty(homeDir))
            homeDir = "/home";

        var vm = new RemoteDirectoryPickerViewModel(_ssh, homeDir);
        var win = new RemoteDirectoryPickerView { DataContext = vm };

        // Set WPF owner to avoid taskbar flicker
        if (Application.Current.MainWindow is { } mainWin)
            win.Owner = mainWin;

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
        }
    }

    private void GenerateNewTaskId()
    {
        TaskId = $"task_{DateTime.Now:yyyyMMdd_HHmmss}";
        // Reset RemoteWorkDir so TryAutoFillRemoteWorkDir can recompute
        RemoteWorkDir = string.Empty;
        TryAutoFillRemoteWorkDir();
    }

    // ── TaskId directory existence validation ─────────────────────────────────

    /// <summary>
    /// Schedules a debounced remote directory existence check for <c>{Root}/{TaskId}/</c>.
    /// Updates <see cref="TaskIdDirectoryStatus"/> and invalidates <see cref="NewTaskIdCommand"/>
    /// CanExecute after a ~300 ms delay to avoid excess SSH round-trips while the user types.
    /// </summary>
    private void ScheduleTaskIdDirectoryCheck()
    {
        // Cancel any in-flight check
        _taskIdValidationCts?.Cancel();
        _taskIdValidationCts?.Dispose();
        _taskIdValidationCts = null;

        // Reset state immediately
        _taskIdDirectoryExists = null;
        TaskIdDirectoryStatus  = string.Empty;
        CommandManager.InvalidateRequerySuggested();

        if (!_ssh.IsConnected
            || string.IsNullOrWhiteSpace(RootDirectory)
            || string.IsNullOrWhiteSpace(TaskId))
        {
            return;
        }

        var cts    = new CancellationTokenSource();
        _taskIdValidationCts = cts;

        var capturedTaskId = TaskId;
        var capturedRoot   = RootDirectory;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, cts.Token);
            }
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
        catch (Exception ex)
        {
            StatusMessage = $"刷新应用路径失败：{ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private void RebuildAppDisplayList(IEnumerable<string>? allCandidates = null)
    {
        var existing = allCandidates ?? AppDisplayList.Where(x => !PinnedApps.Contains(x)).ToList();
        AppDisplayList.Clear();
        foreach (var p in PinnedApps)
            AppDisplayList.Add(p);
        foreach (var c in existing.Where(c => !PinnedApps.Contains(c)))
            AppDisplayList.Add(c);
        RebuildFilteredAppList();
    }

    // ── Autocomplete filtering ────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds <see cref="FilteredAppList"/> from <see cref="AppDisplayList"/> using the
    /// current <see cref="AppPath"/> text as a case-insensitive contains filter.
    /// Prefix matches are placed before contains-only matches.
    /// When the text is empty or an exact item is already selected, all candidates are shown.
    /// </summary>
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

    /// <summary>
    /// Rebuilds <see cref="FilteredTemplateList"/> from <see cref="TemplateDisplayList"/> using the
    /// current <see cref="SelectedTemplate"/> text as a case-insensitive contains filter.
    /// </summary>
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
        if (PinnedApps.Contains(AppPath))
            PinnedApps.Remove(AppPath);
        else
            PinnedApps.Add(AppPath);
        RebuildAppDisplayList();
        SavePins();
    }

    // ── B3: Template candidates + pinning ────────────────────────────────────

    private async Task RefreshTemplateCandidatesAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            var files = await _ssh.ListFilesAsync(RemoteTemplateDir, ct);
            RebuildTemplateDisplayList(files);
        }
        catch (Exception ex)
        {
            StatusMessage = $"刷新模板列表失败：{ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private void RebuildTemplateDisplayList(IEnumerable<string>? allCandidates = null)
    {
        var existing = allCandidates ?? TemplateDisplayList.Where(x => !PinnedTemplates.Contains(x)).ToList();
        TemplateDisplayList.Clear();
        foreach (var p in PinnedTemplates)
            TemplateDisplayList.Add(p);
        foreach (var c in existing.Where(c => !PinnedTemplates.Contains(c)))
            TemplateDisplayList.Add(c);
        RebuildFilteredTemplateList();
    }

    private void TogglePinTemplate()
    {
        if (string.IsNullOrWhiteSpace(SelectedTemplate)) return;
        var t = SelectedTemplate!;
        if (PinnedTemplates.Contains(t))
            PinnedTemplates.Remove(t);
        else
            PinnedTemplates.Add(t);
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
                var content = await _ssh.ReadTextFileAsync(remotePath);
                Application.Current.Dispatcher.Invoke(() => TemplateContent = content);
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                    StatusMessage = $"加载模板失败：{ex.Message}");
            }
        });
    }

    // ── Pin persistence ───────────────────────────────────────────────────────

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

    // ── B4: Parameter file save with Save As + overwrite confirm ─────────────

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
            // Ensure remote params dir exists
            await _ssh.ExecuteAsync($"mkdir -p {EscapeShellArg(remoteParamsDir)}", ct);

            var remoteDest = $"{remoteParamsDir}/{SaveAsFileName}";

            // Overwrite confirmation
            if (await _ssh.RemoteFileExistsAsync(remoteDest, ct))
            {
                var msgText   = Application.Current?.TryFindResource("Task.OverwritePrompt") as string
                                ?? $"远程文件 {remoteDest} 已存在，是否覆盖？";
                var msgTitle  = Application.Current?.TryFindResource("Task.OverwriteTitle") as string ?? "覆盖确认";
                var result = MessageBox.Show(
                    string.Format(msgText, remoteDest),
                    msgTitle,
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question);
                if (result != MessageBoxResult.OK)
                {
                    StatusMessage = Application.Current?.TryFindResource("Task.SaveCancelled") as string ?? "保存已取消。";
                    return;
                }
            }

            await _ssh.WriteTextFileAsync(remoteDest, TemplateContent, ct);
            LastSavedTime = $"已保存：{DateTime.Now:HH:mm:ss}";
            StatusMessage = $"参数文件已保存：{remoteDest}";
        }
        catch (Exception ex) { StatusMessage = $"保存参数文件失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    // ── B5: Task directory file list + remote editor ──────────────────────────

    private async Task RefreshTaskFilesAsync(CancellationToken ct)
    {
        if (!_ssh.IsConnected || string.IsNullOrWhiteSpace(RemoteWorkDir))
        {
            StatusMessage = "请先建立 SSH 连接并设置任务目录。";
            return;
        }

        IsBusy = true;
        try
        {
            // Use `ls -1` to list both files and subdirectories, surfacing errors via exit code
            var (stdout, stderr, exitCode) = await _ssh.ExecuteAsync(
                $"ls -1 {EscapeShellArg(RemoteWorkDir)}", ct);

            TaskFiles.Clear();

            if (exitCode != 0)
            {
                var errDetail = stderr.Trim();
                StatusMessage = string.IsNullOrEmpty(errDetail)
                    ? "无法读取目录，请检查路径和权限。"
                    : $"无法读取目录：{errDetail}";
                return;
            }

            var entries = stdout.Split('\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var entry in entries)
                TaskFiles.Add(entry);

            StatusMessage = TaskFiles.Count == 0 ? "任务目录为空。" : $"已加载 {TaskFiles.Count} 个条目。";
        }
        catch (Exception ex) { StatusMessage = $"刷新文件列表失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    private async Task OpenTaskFileAsync(string? fileName, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(fileName) || string.IsNullOrWhiteSpace(RemoteWorkDir)) return;
        if (!_ssh.IsConnected)
        {
            StatusMessage = "请先建立 SSH 连接。";
            return;
        }

        var remotePath = $"{RemoteWorkDir.TrimEnd('/')}/{fileName}";
        var vm  = new RemoteFileEditorViewModel(_ssh, remotePath);
        var win = new RemoteFileEditorView { DataContext = vm };

        if (Application.Current.MainWindow is { } mainWin)
            win.Owner = mainWin;

        await vm.LoadAsync(ct);
        win.ShowDialog();
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    private string GetLocalTaskDir() => Path.Combine(LocalDataRoot, TaskId);

    private async Task SaveTaskAsync(CancellationToken ct)
    {
        if (!ValidateRootAndId()) return;
        IsBusy = true;
        StatusMessage = "保存任务…";
        try
        {
            Directory.CreateDirectory(GetLocalTaskDir());
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
            var record = await _storage.LoadAsync(LocalDataRoot, TaskId, ct);
            if (record == null) { StatusMessage = "task.json 未找到。"; return; }
            ApplyTaskRecord(record);
            StatusMessage = $"任务已加载：{TaskId}";
        }
        catch (Exception ex) { StatusMessage = $"加载失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    // ── sbatch submit ────────────────────────────────────────────────────────

    private async Task SubmitJobAsync(CancellationToken ct)
    {
        if (!ValidateSubmitRequirements()) return;
        IsBusy = true;
        StatusMessage = "准备 sbatch 脚本…";
        try
        {
            var localTaskDir  = GetLocalTaskDir();
            var scriptsDir    = Path.Combine(localTaskDir, "scripts");
            Directory.CreateDirectory(scriptsDir);
            Directory.CreateDirectory(Path.Combine(localTaskDir, "logs"));

            var paramFile = !string.IsNullOrEmpty(SelectedTemplate)
                ? $"{RemoteWorkDir.TrimEnd('/')}/params/{SelectedTemplate}".Replace('\\', '/')
                : string.Empty;

            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["JOB_NAME"]    = TaskId,
                ["WORK_DIR"]    = RemoteWorkDir,
                ["APP_PATH"]    = AppPath,
                ["PARAM_FILE"]  = paramFile,
                ["STDOUT_FILE"] = $"{RemoteWorkDir}/logs/job.out",
                ["STDERR_FILE"] = $"{RemoteWorkDir}/logs/job.err",
            };
            foreach (var p in Parameters.Where(p => !string.IsNullOrWhiteSpace(p.Key)))
                parameters[p.Key] = p.Value;

            var rendered    = new SbatchTemplateRenderer(SbatchTemplate).Render(parameters);
            var localScript = Path.Combine(scriptsDir, "submit.sbatch");
            await File.WriteAllTextAsync(localScript, rendered, ct);

            StatusMessage = "上传并提交中…";
            var jobId = await _slurm.SubmitSbatchAsync(localScript, RemoteWorkDir, ct);
            LastJobId = jobId;

            await File.WriteAllTextAsync(
                Path.Combine(localTaskDir, "logs", "submit.log"),
                $"Submitted at {DateTime.UtcNow:u}\nJob ID: {jobId}\n",
                ct);

            var record = (await _storage.LoadAsync(LocalDataRoot, TaskId, ct)) ?? BuildTaskRecord();
            record.SlurmJobId = jobId;
            record.UpdatedAt  = DateTime.UtcNow;
            await _storage.SaveAsync(record, ct);

            StatusMessage = $"作业已提交！Job ID = {jobId}";
        }
        catch (Exception ex) { StatusMessage = $"提交失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private bool CanSubmit() =>
        !IsBusy
        && _ssh.IsConnected
        && !string.IsNullOrWhiteSpace(RootDirectory)
        && !string.IsNullOrWhiteSpace(TaskId)
        && !string.IsNullOrWhiteSpace(AppPath);

    private bool ValidateRootAndId()
    {
        if (string.IsNullOrWhiteSpace(RootDirectory))
        {
            StatusMessage = "请先配置任务根目录";
            return false;
        }
        if (string.IsNullOrWhiteSpace(TaskId))
        {
            StatusMessage = "请先填写 TaskId";
            return false;
        }
        return true;
    }

    private bool ValidateSubmitRequirements()
    {
        if (!ValidateRootAndId()) return false;
        if (string.IsNullOrWhiteSpace(AppPath))
        {
            StatusMessage = "请先选择应用程序路径";
            return false;
        }
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
        LastJobId        = r.SlurmJobId;
        SelectedTemplate = r.TemplateFileName;
        Parameters.Clear();
        foreach (var (k, v) in r.Parameters)
            Parameters.Add(new ParameterEntry { Key = k, Value = v });
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
