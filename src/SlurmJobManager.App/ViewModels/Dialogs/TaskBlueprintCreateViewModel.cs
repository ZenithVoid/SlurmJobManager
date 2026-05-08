using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using SlurmJobManager.App.Views.Dialogs;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Models;

namespace SlurmJobManager.App.ViewModels.Dialogs;

public sealed class TaskBlueprintCreateViewModel : ViewModelBase
{
    private readonly ITaskBlueprintService _blueprintService;
    private readonly TaskBlueprintScope _scope;

    private TaskBlueprintSummary? _selectedBlueprint;
    private TaskBlueprintRecord? _selectedBlueprintRecord;
    private string _searchText = string.Empty;
    private string _editName = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _isBusy;
    private int _selectionLoadVersion;

    public ObservableCollection<TaskBlueprintSummary> Blueprints { get; } = new();
    public ICollectionView FilteredBlueprints { get; }

    public TaskBlueprintSummary? SelectedBlueprint
    {
        get => _selectedBlueprint;
        set
        {
            if (!SetField(ref _selectedBlueprint, value))
                return;

            _ = LoadSelectedBlueprintRecordAsync();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetField(ref _searchText, value))
                return;

            FilteredBlueprints.Refresh();
            EnsureSelectionVisible();
        }
    }

    public string EditName
    {
        get => _editName;
        set
        {
            if (SetField(ref _editName, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public string SelectedBlueprintName => SelectedBlueprintRecord?.Name ?? string.Empty;
    public string SelectedBlueprintDescription => SelectedBlueprintRecord?.Description ?? string.Empty;
    public string SelectedBlueprintUpdatedAt
        => SelectedBlueprintRecord == null
            ? string.Empty
            : string.Format(L("Task.BlueprintUpdatedAtValue"), SelectedBlueprintRecord.UpdatedAt.ToLocalTime());

    public string PreviewProgramPath => GetFirstProgramPath(SelectedBlueprintRecord) ?? "-";
    public string PreviewWorkDirectory => GetFirstWorkDirectory(SelectedBlueprintRecord) ?? "-";
    public string PreviewJobName => GetFirstJobName(SelectedBlueprintRecord) ?? "-";
    public string PreviewQueue => GetFirstQueue(SelectedBlueprintRecord) ?? "-";
    public string PreviewNodes => GetFirstNodes(SelectedBlueprintRecord) ?? "-";
    public string PreviewCpuCount => GetFirstCpuCount(SelectedBlueprintRecord) ?? "-";
    public string PreviewAccount => GetFirstAccount(SelectedBlueprintRecord) ?? "-";
    public string PreviewParamFileCount => CountParameterFiles(SelectedBlueprintRecord).ToString();
    public string PreviewParamFileCountText
        => string.Format(L("Task.BlueprintParamFilesCount"), CountParameterFiles(SelectedBlueprintRecord));

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public TaskBlueprintRecord? AppliedBlueprintRecord { get; private set; }
    public TaskBlueprintRecord? SelectedBlueprintRecord
    {
        get => _selectedBlueprintRecord;
        private set
        {
            if (!SetField(ref _selectedBlueprintRecord, value))
                return;
            NotifyPreviewChanged();
        }
    }

    public bool ApplyRequested { get; private set; }
    public bool Confirmed { get; private set; }

    public ICommand RefreshCommand { get; }
    public ICommand RenameBlueprintCommand { get; }
    public ICommand DuplicateBlueprintCommand { get; }
    public ICommand DeleteBlueprintCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand CancelCommand { get; }

    public TaskBlueprintCreateViewModel(ITaskBlueprintService blueprintService, TaskBlueprintScope scope)
    {
        _blueprintService = blueprintService ?? throw new ArgumentNullException(nameof(blueprintService));
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));

        FilteredBlueprints = CollectionViewSource.GetDefaultView(Blueprints);
        FilteredBlueprints.Filter = FilterBlueprint;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        RenameBlueprintCommand = new AsyncRelayCommand(RenameSelectedAsync, () => !IsBusy && SelectedBlueprint != null && !string.IsNullOrWhiteSpace(EditName));
        DuplicateBlueprintCommand = new AsyncRelayCommand(DuplicateSelectedAsync, () => !IsBusy && SelectedBlueprint != null);
        DeleteBlueprintCommand = new AsyncRelayCommand(DeleteSelectedAsync, () => !IsBusy && SelectedBlueprint != null);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => !IsBusy && SelectedBlueprint != null);
        CancelCommand = new RelayCommand(() => Confirmed = false);
    }

    public async Task LoadAsync(CancellationToken ct = default)
        => await RefreshAsync(ct);

    public async Task<bool> ConfirmApplyAsync(CancellationToken ct = default)
    {
        await ApplyAsync(ct);
        return Confirmed && ApplyRequested && AppliedBlueprintRecord != null;
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            Confirmed = false;
            ApplyRequested = false;
            AppliedBlueprintRecord = null;

            var list = await _blueprintService.ListAsync(_scope, ct);
            var selectedId = SelectedBlueprint?.BlueprintId;

            Blueprints.Clear();
            foreach (var item in list)
                Blueprints.Add(item);

            FilteredBlueprints.Refresh();

            if (Blueprints.Count == 0)
            {
                SelectedBlueprint = null;
                SelectedBlueprintRecord = null;
                EditName = string.Empty;
                StatusMessage = L("Task.BlueprintListEmpty");
                return;
            }

            SelectedBlueprint = Blueprints.FirstOrDefault(x => string.Equals(x.BlueprintId, selectedId, StringComparison.OrdinalIgnoreCase))
                ?? FilteredBlueprints.Cast<TaskBlueprintSummary>().FirstOrDefault()
                ?? Blueprints[0];
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(L("Task.BlueprintListFailed"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RenameSelectedAsync(CancellationToken ct)
    {
        if (SelectedBlueprint == null)
        {
            StatusMessage = L("Task.BlueprintSelectRequired");
            return;
        }

        var nextName = EditName.Trim();
        if (string.IsNullOrWhiteSpace(nextName))
        {
            StatusMessage = L("Task.BlueprintNameRequired");
            return;
        }

        IsBusy = true;
        try
        {
            var record = await _blueprintService.LoadAsync(SelectedBlueprint.BlueprintId, _scope, ct);
            if (record == null)
            {
                StatusMessage = L("Task.BlueprintLoadMissing");
                await RefreshAsync(ct);
                return;
            }

            record.Name = nextName;
            record.UpdatedAt = DateTime.UtcNow;
            await _blueprintService.SaveAsync(record, _scope, overwriteByName: false, ct);
            StatusMessage = string.Format(L("Task.BlueprintRenameSucceeded"), nextName);
            await RefreshAsync(ct);
            SelectedBlueprint = Blueprints.FirstOrDefault(x => string.Equals(x.BlueprintId, record.BlueprintId, StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidOperationException)
        {
            StatusMessage = string.Format(L("Task.BlueprintRenameConflict"), nextName);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(L("Task.BlueprintRenameFailed"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DuplicateSelectedAsync(CancellationToken ct)
    {
        if (SelectedBlueprint == null)
        {
            StatusMessage = L("Task.BlueprintSelectRequired");
            return;
        }

        IsBusy = true;
        try
        {
            var source = await _blueprintService.LoadAsync(SelectedBlueprint.BlueprintId, _scope, ct);
            if (source == null)
            {
                StatusMessage = L("Task.BlueprintLoadMissing");
                await RefreshAsync(ct);
                return;
            }

            var candidate = string.IsNullOrWhiteSpace(EditName)
                ? BuildDefaultCopyName(source.Name, Blueprints.Select(x => x.Name))
                : EditName.Trim();

            var clone = CloneBlueprint(source);
            clone.BlueprintId = Guid.NewGuid().ToString("N");
            clone.Name = candidate;
            clone.CreatedAt = DateTime.UtcNow;
            clone.UpdatedAt = DateTime.UtcNow;

            await _blueprintService.SaveAsync(clone, _scope, overwriteByName: false, ct);
            StatusMessage = string.Format(L("Task.BlueprintDuplicateSucceeded"), clone.Name);
            await RefreshAsync(ct);
            SelectedBlueprint = Blueprints.FirstOrDefault(x => string.Equals(x.BlueprintId, clone.BlueprintId, StringComparison.OrdinalIgnoreCase));
            EditName = clone.Name;
        }
        catch (InvalidOperationException)
        {
            StatusMessage = string.Format(L("Task.BlueprintDuplicateConflict"), EditName.Trim());
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(L("Task.BlueprintDuplicateFailed"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteSelectedAsync(CancellationToken ct)
    {
        if (SelectedBlueprint == null)
            return;

        var confirmVm = new ConfirmationDialogViewModel(
            title: L("Task.BlueprintDeleteTitle"),
            message: string.Format(L("Task.BlueprintDeleteConfirm"), SelectedBlueprint.Name),
            details: L("Task.BlueprintDeleteDetail"),
            confirmButtonText: L("Task.BlueprintDelete"),
            cancelButtonText: L("Btn.Cancel"),
            isWarning: true);
        if (!ShowConfirmationDialog(confirmVm))
            return;

        IsBusy = true;
        try
        {
            var success = await _blueprintService.DeleteAsync(SelectedBlueprint.BlueprintId, _scope, ct);
            if (!success)
            {
                StatusMessage = L("Task.BlueprintDeleteFailed");
                return;
            }

            StatusMessage = L("Task.BlueprintDeleteSucceeded");
            await RefreshAsync(ct);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(L("Task.BlueprintDeleteFailedDetail"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ApplyAsync(CancellationToken ct)
    {
        if (SelectedBlueprint == null)
        {
            StatusMessage = L("Task.BlueprintSelectRequired");
            return;
        }

        IsBusy = true;
        try
        {
            var blueprint = await _blueprintService.LoadAsync(SelectedBlueprint.BlueprintId, _scope, ct);
            if (blueprint == null)
            {
                StatusMessage = L("Task.BlueprintLoadMissing");
                return;
            }

            AppliedBlueprintRecord = blueprint;
            ApplyRequested = true;
            Confirmed = true;
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(L("Task.BlueprintLoadFailed"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadSelectedBlueprintRecordAsync()
    {
        var selected = SelectedBlueprint;
        if (selected == null)
        {
            SelectedBlueprintRecord = null;
            EditName = string.Empty;
            return;
        }

        var version = Interlocked.Increment(ref _selectionLoadVersion);
        try
        {
            var loaded = await _blueprintService.LoadAsync(selected.BlueprintId, _scope, CancellationToken.None);
            if (version != _selectionLoadVersion)
                return;

            SelectedBlueprintRecord = loaded;
            EditName = loaded?.Name ?? string.Empty;
        }
        catch
        {
            if (version != _selectionLoadVersion)
                return;

            SelectedBlueprintRecord = null;
            EditName = string.Empty;
        }
    }

    private void NotifyPreviewChanged()
    {
        OnPropertyChanged(nameof(SelectedBlueprintName));
        OnPropertyChanged(nameof(SelectedBlueprintDescription));
        OnPropertyChanged(nameof(SelectedBlueprintUpdatedAt));
        OnPropertyChanged(nameof(PreviewProgramPath));
        OnPropertyChanged(nameof(PreviewWorkDirectory));
        OnPropertyChanged(nameof(PreviewJobName));
        OnPropertyChanged(nameof(PreviewQueue));
        OnPropertyChanged(nameof(PreviewNodes));
        OnPropertyChanged(nameof(PreviewCpuCount));
        OnPropertyChanged(nameof(PreviewAccount));
        OnPropertyChanged(nameof(PreviewParamFileCount));
        OnPropertyChanged(nameof(PreviewParamFileCountText));
        CommandManager.InvalidateRequerySuggested();
    }

    private bool FilterBlueprint(object obj)
    {
        if (obj is not TaskBlueprintSummary summary)
            return false;

        if (string.IsNullOrWhiteSpace(SearchText))
            return true;

        return summary.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureSelectionVisible()
    {
        if (FilteredBlueprints.IsEmpty)
        {
            SelectedBlueprint = null;
            return;
        }

        if (SelectedBlueprint != null && FilterBlueprint(SelectedBlueprint))
            return;

        SelectedBlueprint = FilteredBlueprints.Cast<TaskBlueprintSummary>().FirstOrDefault();
    }

    private string BuildDefaultCopyName(string sourceName, IEnumerable<string> existingNames)
    {
        var suffix = L("Task.BlueprintCopySuffix");
        var baseName = $"{sourceName} - {suffix}";
        var names = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName))
            return baseName;

        for (var i = 2; i <= 9999; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!names.Contains(candidate))
                return candidate;
        }

        return $"{baseName} ({DateTime.UtcNow:yyyyMMddHHmmss})";
    }

    private static TaskBlueprintRecord CloneBlueprint(TaskBlueprintRecord source)
    {
        var json = JsonSerializer.Serialize(source);
        return JsonSerializer.Deserialize<TaskBlueprintRecord>(json) ?? new TaskBlueprintRecord();
    }

    private static string? GetFirstProgramPath(TaskBlueprintRecord? record)
        => record?.TaskUnits
            .SelectMany(x => x.ProgramEntries)
            .Select(x => x.ProgramPath?.Trim())
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

    private static string? GetFirstWorkDirectory(TaskBlueprintRecord? record)
        => record == null
            ? null
            : (record.RemoteWorkDirectory?.Trim() is { Length: > 0 } root
                ? root
                : record.TaskUnits.Select(x => x.RemoteWorkDirectory?.Trim()).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)));

    private static string? GetFirstJobName(TaskBlueprintRecord? record)
        => record?.TaskUnits
            .Select(x => x.SbatchOptions?.JobName?.Trim())
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
           ?? record?.ActiveTaskUnitName?.Trim();

    private static string? GetFirstQueue(TaskBlueprintRecord? record)
        => record?.TaskUnits
            .Select(x => x.SbatchOptions?.Partition?.Trim())
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

    private static string? GetFirstNodes(TaskBlueprintRecord? record)
        => record?.TaskUnits
            .Select(x => x.SbatchOptions?.Nodes?.Trim())
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && x != "0");

    private static string? GetFirstCpuCount(TaskBlueprintRecord? record)
        => record?.TaskUnits
            .Select(x => x.SbatchOptions?.CpuCount?.Trim())
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && x != "0");

    private static string? GetFirstAccount(TaskBlueprintRecord? record)
        => record?.TaskUnits
            .Select(x => x.SbatchOptions?.Account?.Trim())
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

    private static int CountParameterFiles(TaskBlueprintRecord? record)
    {
        if (record == null)
            return 0;

        return record.TaskUnits
            .SelectMany(unit => unit.ParameterFiles.Select(x => x.FilePath)
                .Concat(unit.CommandEntries.SelectMany(c => c.ParameterFiles)))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private bool ShowConfirmationDialog(ConfirmationDialogViewModel viewModel)
    {
        var dialog = new ConfirmationDialogView { DataContext = viewModel };
        if (Application.Current.MainWindow is { } mainWindow)
            dialog.Owner = mainWindow;
        return dialog.ShowDialog() == true;
    }

    private static string L(string key)
        => Application.Current?.TryFindResource(key) as string ?? key;
}
