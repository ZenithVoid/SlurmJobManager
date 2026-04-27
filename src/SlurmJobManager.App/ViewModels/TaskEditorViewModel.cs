using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
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

    private string _rootDirectory = string.Empty;
    private string _taskId = string.Empty;
    private string _templateDirectory = string.Empty;
    private string? _selectedTemplate;
    private string _templateContent = string.Empty;
    private string _lastSavedTime = string.Empty;
    private string _remoteWorkDir = string.Empty;
    private string _appPath = string.Empty;
    private string _sbatchTemplate = DefaultSbatchTemplate;
    private bool _isBusy;
    private string _statusMessage = string.Empty;
    private long? _lastJobId;

    public string RootDirectory
    {
        get => _rootDirectory;
        set { if (SetField(ref _rootDirectory, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public string TaskId
    {
        get => _taskId;
        set { if (SetField(ref _taskId, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public string TemplateDirectory { get => _templateDirectory; set { if (SetField(ref _templateDirectory, value)) RefreshTemplateList(); } }
    public string? SelectedTemplate { get => _selectedTemplate;  set { if (SetField(ref _selectedTemplate, value)) LoadSelectedTemplate(); } }
    public string TemplateContent   { get => _templateContent;   set => SetField(ref _templateContent, value); }
    public string LastSavedTime     { get => _lastSavedTime;     set => SetField(ref _lastSavedTime, value); }
    public string RemoteWorkDir     { get => _remoteWorkDir;     set => SetField(ref _remoteWorkDir, value); }

    public string AppPath
    {
        get => _appPath;
        set { if (SetField(ref _appPath, value)) CommandManager.InvalidateRequerySuggested(); }
    }
    public string SbatchTemplate    { get => _sbatchTemplate;    set => SetField(ref _sbatchTemplate, value); }
    public bool IsBusy              { get => _isBusy;            set => SetField(ref _isBusy, value); }
    public string StatusMessage     { get => _statusMessage;     set => SetField(ref _statusMessage, value); }
    public long? LastJobId          { get => _lastJobId;         set { SetField(ref _lastJobId, value); OnPropertyChanged(nameof(LastJobIdText)); } }
    public string LastJobIdText     => _lastJobId.HasValue ? $"Last Job ID: {_lastJobId}" : string.Empty;

    public ObservableCollection<string> TemplateFiles { get; } = new();
    public ObservableCollection<ParameterEntry> Parameters { get; } = new();

    public ICommand BrowseRootDirectoryCommand     { get; }
    public ICommand BrowseTemplateDirectoryCommand { get; }
    public ICommand NewTaskIdCommand               { get; }
    public ICommand SaveTaskCommand                { get; }
    public ICommand LoadTaskCommand                { get; }
    public ICommand SaveParamFileCommand           { get; }
    public ICommand SubmitJobCommand               { get; }
    public ICommand AddParamCommand                { get; }
    public ICommand RemoveParamCommand             { get; }

    public TaskEditorViewModel(ISshClientService ssh, ISlurmService slurm, ITaskStorageService storage)
    {
        _ssh     = ssh     ?? throw new ArgumentNullException(nameof(ssh));
        _slurm   = slurm   ?? throw new ArgumentNullException(nameof(slurm));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));

        BrowseRootDirectoryCommand     = new RelayCommand(BrowseRootDirectory);
        BrowseTemplateDirectoryCommand = new RelayCommand(BrowseTemplateDirectory);
        NewTaskIdCommand               = new RelayCommand(GenerateNewTaskId);
        SaveTaskCommand                = new AsyncRelayCommand(SaveTaskAsync,     () => !IsBusy);
        LoadTaskCommand                = new AsyncRelayCommand(LoadTaskAsync,     () => !IsBusy);
        SaveParamFileCommand           = new AsyncRelayCommand(SaveParamFileAsync, () => !IsBusy);
        SubmitJobCommand               = new AsyncRelayCommand(SubmitJobAsync,    CanSubmit);
        AddParamCommand                = new RelayCommand(() => Parameters.Add(new ParameterEntry()));
        RemoveParamCommand             = new RelayCommand<ParameterEntry>(p => { if (p != null) Parameters.Remove(p); });
    }

    // ── Directory browsing ───────────────────────────────────────────────────

    private void BrowseRootDirectory()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Task Root Directory",
        };
        if (dlg.ShowDialog() == true)
            RootDirectory = dlg.FolderName;
    }

    private void BrowseTemplateDirectory()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Parameter Template Directory",
        };
        if (dlg.ShowDialog() == true)
            TemplateDirectory = dlg.FolderName;
    }

    private void GenerateNewTaskId() =>
        TaskId = $"task_{DateTime.Now:yyyyMMdd_HHmmss}";

    // ── Template management ──────────────────────────────────────────────────

    private static readonly HashSet<string> TemplateExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".json", ".yaml", ".yml", ".txt", ".conf" };

    private void RefreshTemplateList()
    {
        TemplateFiles.Clear();
        if (!Directory.Exists(_templateDirectory)) return;
        foreach (var f in Directory.EnumerateFiles(_templateDirectory)
                     .Where(f => TemplateExtensions.Contains(Path.GetExtension(f))))
            TemplateFiles.Add(Path.GetFileName(f));
    }

    private void LoadSelectedTemplate()
    {
        if (string.IsNullOrEmpty(_selectedTemplate) || !Directory.Exists(_templateDirectory)) return;
        var path = Path.Combine(_templateDirectory, _selectedTemplate);
        if (File.Exists(path))
            TemplateContent = File.ReadAllText(path);
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    private async Task SaveTaskAsync(CancellationToken ct)
    {
        if (!ValidateRootAndId()) return;
        IsBusy = true;
        StatusMessage = "Saving task…";
        try
        {
            EnsureTaskDirectories();
            await _storage.SaveAsync(BuildTaskRecord(), ct);
            StatusMessage = $"Task saved: {_storage.GetTaskDirectory(RootDirectory, TaskId)}";
        }
        catch (Exception ex) { StatusMessage = $"Save failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private async Task LoadTaskAsync(CancellationToken ct)
    {
        if (!ValidateRootAndId()) return;
        IsBusy = true;
        StatusMessage = "Loading task…";
        try
        {
            var record = await _storage.LoadAsync(RootDirectory, TaskId, ct);
            if (record == null) { StatusMessage = "task.json not found."; return; }
            ApplyTaskRecord(record);
            StatusMessage = $"Task loaded: {TaskId}";
        }
        catch (Exception ex) { StatusMessage = $"Load failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    // ── Parameter file ───────────────────────────────────────────────────────

    private async Task SaveParamFileAsync(CancellationToken ct)
    {
        if (!ValidateRootAndId() || string.IsNullOrWhiteSpace(SelectedTemplate))
        {
            StatusMessage = "请先完成必填项后再保存参数文件（根目录、TaskId 和模板不可为空）。";
            return;
        }

        IsBusy = true;
        try
        {
            var paramsDir = Path.Combine(_storage.GetTaskDirectory(RootDirectory, TaskId), "params");
            Directory.CreateDirectory(paramsDir);
            var dest = Path.Combine(paramsDir, SelectedTemplate);
            await File.WriteAllTextAsync(dest, TemplateContent, ct);
            LastSavedTime = $"Saved: {DateTime.Now:HH:mm:ss}";
            StatusMessage = $"Parameter file saved: {dest}";
        }
        catch (Exception ex) { StatusMessage = $"Save param failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    // ── sbatch submit ────────────────────────────────────────────────────────

    private async Task SubmitJobAsync(CancellationToken ct)
    {
        if (!ValidateSubmitRequirements()) return;
        IsBusy = true;
        StatusMessage = "Preparing sbatch script…";
        try
        {
            EnsureTaskDirectories();
            var taskDir    = _storage.GetTaskDirectory(RootDirectory, TaskId);
            var scriptsDir = Path.Combine(taskDir, "scripts");
            Directory.CreateDirectory(scriptsDir);

            var paramFile = SelectedTemplate != null
                ? Path.Combine(RemoteWorkDir, "params", SelectedTemplate).Replace('\\', '/')
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

            var renderer    = new SbatchTemplateRenderer(SbatchTemplate);
            var rendered    = renderer.Render(parameters);
            var localScript = Path.Combine(scriptsDir, "submit.sbatch");
            await File.WriteAllTextAsync(localScript, rendered, ct);

            StatusMessage = "Uploading and submitting…";
            var jobId = await _slurm.SubmitSbatchAsync(localScript, RemoteWorkDir, ct);
            LastJobId = jobId;

            // Write submission log
            var logsDir = Path.Combine(taskDir, "logs");
            Directory.CreateDirectory(logsDir);
            await File.WriteAllTextAsync(
                Path.Combine(logsDir, "submit.log"),
                $"Submitted at {DateTime.UtcNow:u}\nJob ID: {jobId}\n",
                ct);

            // Persist job id back to task.json
            var record = (await _storage.LoadAsync(RootDirectory, TaskId, ct)) ?? BuildTaskRecord();
            record.SlurmJobId = jobId;
            record.UpdatedAt  = DateTime.UtcNow;
            await _storage.SaveAsync(record, ct);

            StatusMessage = $"Job submitted! Job ID = {jobId}";
        }
        catch (Exception ex) { StatusMessage = $"Submit failed: {ex.Message}"; }
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

    private void EnsureTaskDirectories()
    {
        var taskDir = _storage.GetTaskDirectory(RootDirectory, TaskId);
        foreach (var sub in new[] { "params", "scripts", "logs", "result-cache" })
            Directory.CreateDirectory(Path.Combine(taskDir, sub));
    }

    private TaskRecord BuildTaskRecord() => new()
    {
        TaskId              = TaskId,
        LocalRootDirectory  = RootDirectory,
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

