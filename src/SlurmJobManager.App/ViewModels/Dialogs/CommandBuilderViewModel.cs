using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using SlurmJobManager.App.Views;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Models;

namespace SlurmJobManager.App.ViewModels.Dialogs;

/// <summary>
/// View-model for the Command Builder dialog.
/// Lets the user pick a remote program and attach one or more remote parameter
/// files, then previews the resulting command line before applying it back to the
/// active task unit.
/// </summary>
public sealed class CommandBuilderViewModel : ViewModelBase
{
    // ── Remote source directories (mirrors TaskEditorViewModel) ────────────
    private static readonly string[] AppSourceDirs = { "/env/preprocess/out", "/env/preprocess/bin" };
    private const string RemoteParamDir = "/env/preprocess/out/config";

    private readonly ISshClientService _ssh;

    private string _selectedProgram = string.Empty;
    private string _selectedAvailableParamFile = string.Empty;
    private string _programFilter = string.Empty;
    private string _paramFilter = string.Empty;
    private string _commandPreview = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _isBusy;

    // ── Result (set on Confirm) ─────────────────────────────────────────────
    public bool Confirmed { get; private set; }

    // ── Available lists (loaded from remote) ───────────────────────────────
    public ObservableCollection<string> AllPrograms   { get; } = new();
    public ObservableCollection<string> FilteredPrograms { get; } = new();
    public ObservableCollection<string> AllParamFiles   { get; } = new();
    public ObservableCollection<string> FilteredParamFiles { get; } = new();

    // ── Selected items ──────────────────────────────────────────────────────
    /// <summary>The remote program path chosen by the user.</summary>
    public string SelectedProgram
    {
        get => _selectedProgram;
        set
        {
            if (SetField(ref _selectedProgram, value))
            {
                RebuildCommandPreview();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>Highlighted item in the available parameter-file list.</summary>
    public string SelectedAvailableParamFile
    {
        get => _selectedAvailableParamFile;
        set
        {
            if (SetField(ref _selectedAvailableParamFile, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Currently highlighted entry in the chosen parameter-file list.</summary>
    public ParameterFileEntryViewModel? SelectedChosenParamFile { get; set; }

    /// <summary>Ordered list of parameter files the user has attached.</summary>
    public ObservableCollection<ParameterFileEntryViewModel> ChosenParamFiles { get; } = new();

    // ── Filter texts ────────────────────────────────────────────────────────
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

    // ── Command preview ─────────────────────────────────────────────────────
    public string CommandPreview
    {
        get => _commandPreview;
        private set => SetField(ref _commandPreview, value);
    }

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

    // ── Commands ────────────────────────────────────────────────────────────
    public ICommand RefreshCommand       { get; }
    public ICommand AddParamFileCommand  { get; }
    public ICommand RemoveParamFileCommand { get; }
    public ICommand EditParamFileCommand { get; }
    public ICommand TestCommand          { get; }
    public ICommand ConfirmCommand       { get; }

    // ── Constructor ─────────────────────────────────────────────────────────

    public CommandBuilderViewModel(
        ISshClientService ssh,
        string initialProgram = "",
        IEnumerable<ParameterFileEntry>? initialParamFiles = null)
    {
        _ssh            = ssh ?? throw new ArgumentNullException(nameof(ssh));
        _selectedProgram = initialProgram;

        if (initialParamFiles != null)
        {
            foreach (var pf in initialParamFiles)
                ChosenParamFiles.Add(new ParameterFileEntryViewModel(pf));
        }

        RefreshCommand        = new AsyncRelayCommand(RefreshAsync, () => ssh.IsConnected && !IsBusy);
        AddParamFileCommand   = new RelayCommand(AddParamFile, () => !string.IsNullOrWhiteSpace(_selectedAvailableParamFile));
        RemoveParamFileCommand = new RelayCommand<ParameterFileEntryViewModel>(RemoveParamFile);
        EditParamFileCommand  = new AsyncRelayCommand<ParameterFileEntryViewModel>(EditParamFileAsync);
        TestCommand           = new AsyncRelayCommand(TestCommandAsync, () => ssh.IsConnected && !IsBusy && !string.IsNullOrWhiteSpace(_selectedProgram));
        ConfirmCommand        = new RelayCommand(Confirm, () => !string.IsNullOrWhiteSpace(_selectedProgram));

        RebuildCommandPreview();
    }

    // ── Remote refresh ──────────────────────────────────────────────────────

    public async Task LoadInitialAsync(CancellationToken ct = default)
        => await RefreshAsync(ct);

    private async Task RefreshAsync(CancellationToken ct)
    {
        IsBusy = true;
        StatusMessage = "加载远程文件列表…";
        try
        {
            // Load programs
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
                    System.Diagnostics.Debug.WriteLine($"[CommandBuilderViewModel] ListFilesAsync({dir}): {ex.Message}");
                }
            }
            AllPrograms.Clear();
            foreach (var p in allPrograms) AllPrograms.Add(p);
            RebuildFilteredPrograms();

            // Load param files
            try
            {
                var paramFiles = await _ssh.ListFilesAsync(RemoteParamDir, ct);
                AllParamFiles.Clear();
                foreach (var f in paramFiles) AllParamFiles.Add($"{RemoteParamDir}/{f}");
                RebuildFilteredParamFiles();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CommandBuilderViewModel] ListFilesAsync({RemoteParamDir}): {ex.Message}");
            }

            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"刷新失败：{ex.Message}";
        }
        finally { IsBusy = false; }
    }

    // ── Filter helpers ──────────────────────────────────────────────────────

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

    // ── Param file management ───────────────────────────────────────────────

    private void AddParamFile()
    {
        var path = _selectedAvailableParamFile;
        if (string.IsNullOrWhiteSpace(path)) return;
        // Avoid duplicates
        if (ChosenParamFiles.Any(f => f.FilePath == path)) return;

        var alias = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        ChosenParamFiles.Add(new ParameterFileEntryViewModel(
            new ParameterFileEntry { FilePath = path, Alias = alias }));

        RebuildCommandPreview();
    }

    private void RemoveParamFile(ParameterFileEntryViewModel? item)
    {
        if (item == null) return;
        ChosenParamFiles.Remove(item);
        RebuildCommandPreview();
    }

    // ── Double-click edit param file ────────────────────────────────────────

    private async Task EditParamFileAsync(ParameterFileEntryViewModel? item, CancellationToken ct)
    {
        if (item == null || string.IsNullOrEmpty(item.FilePath)) return;

        if (!_ssh.IsConnected)
        {
            StatusMessage = "请先建立 SSH 连接后再编辑文件。";
            return;
        }

        // Verify file exists on remote before opening editor
        try
        {
            var exists = await _ssh.RemoteFileExistsAsync(item.FilePath, ct);
            if (!exists)
            {
                StatusMessage = $"远程文件不存在：{item.FilePath}";
                return;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"检查文件存在性失败：{ex.Message}";
            return;
        }

        var vm  = new RemoteFileEditorViewModel(_ssh, item.FilePath);
        var win = new RemoteFileEditorView { DataContext = vm };
        if (Application.Current.MainWindow is { } mainWin)
            win.Owner = mainWin;

        await vm.LoadAsync(ct);
        win.ShowDialog();
    }

    // ── Command preview ─────────────────────────────────────────────────────

    private void RebuildCommandPreview()
    {
        if (string.IsNullOrWhiteSpace(_selectedProgram))
        {
            CommandPreview = string.Empty;
            return;
        }

        var parts = new List<string> { _selectedProgram };
        foreach (var pf in ChosenParamFiles.Where(f => !string.IsNullOrWhiteSpace(f.FilePath)))
            parts.Add(pf.FilePath);

        CommandPreview = string.Join(" \\\n    ", parts);
    }

    // ── Test command ────────────────────────────────────────────────────────

    private async Task TestCommandAsync(CancellationToken ct)
    {
        if (!_ssh.IsConnected || string.IsNullOrWhiteSpace(_selectedProgram)) return;

        IsBusy = true;
        StatusMessage = "测试中…";
        try
        {
            var (stdout, stderr, exitCode) = await _ssh.ExecuteAsync(
                $"{_selectedProgram} --help 2>&1 | head -20", ct);

            var output = (stdout + stderr).Trim();
            StatusMessage = exitCode == 0
                ? $"命令可访问（退出码 0）：{(output.Length > 100 ? output[..100] + "…" : output)}"
                : $"命令返回退出码 {exitCode}：{(output.Length > 100 ? output[..100] + "…" : output)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"测试失败：{ex.Message}";
        }
        finally { IsBusy = false; }
    }

    // ── Confirm / apply ─────────────────────────────────────────────────────

    private void Confirm()
    {
        Confirmed = true;
    }

    // ── Snapshot for apply-back ─────────────────────────────────────────────

    /// <summary>Returns the selected program path (to apply to the active task unit).</summary>
    public string GetResultProgram() => _selectedProgram;

    /// <summary>Returns the chosen parameter file entries (to apply to the active task unit).</summary>
    public IReadOnlyList<ParameterFileEntry> GetResultParamFiles()
        => ChosenParamFiles.Select(f => f.ToModel()).ToList();
}
