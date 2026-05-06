using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Models;

namespace SlurmJobManager.App.ViewModels.Dialogs;

public sealed class TaskBlueprintCreateViewModel : ViewModelBase
{
    private readonly ITaskBlueprintService _blueprintService;
    private readonly TaskBlueprintScope _scope;

    private TaskBlueprintSummary? _selectedBlueprint;
    private string _newTaskId = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _isBusy;

    public ObservableCollection<TaskBlueprintSummary> Blueprints { get; } = new();

    public TaskBlueprintSummary? SelectedBlueprint
    {
        get => _selectedBlueprint;
        set
        {
            if (SetField(ref _selectedBlueprint, value))
            {
                OnPropertyChanged(nameof(SelectedBlueprintDescription));
                OnPropertyChanged(nameof(SelectedBlueprintUpdatedAt));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string SelectedBlueprintDescription
        => SelectedBlueprint?.Description ?? string.Empty;

    public string SelectedBlueprintUpdatedAt
        => SelectedBlueprint == null
            ? string.Empty
            : string.Format(L("Task.BlueprintUpdatedAtValue"), SelectedBlueprint.UpdatedAt.ToLocalTime());

    public string NewTaskId
    {
        get => _newTaskId;
        set
        {
            if (SetField(ref _newTaskId, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

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

    public TaskBlueprintRecord? SelectedBlueprintRecord { get; private set; }
    public bool Confirmed { get; private set; }

    public ICommand RefreshCommand { get; }
    public ICommand DeleteBlueprintCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand CancelCommand { get; }

    public TaskBlueprintCreateViewModel(ITaskBlueprintService blueprintService, TaskBlueprintScope scope)
    {
        _blueprintService = blueprintService ?? throw new ArgumentNullException(nameof(blueprintService));
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        DeleteBlueprintCommand = new AsyncRelayCommand(DeleteSelectedAsync, () => !IsBusy && SelectedBlueprint != null);
        CreateCommand = new AsyncRelayCommand(CreateAsync, () => !IsBusy && SelectedBlueprint != null && !string.IsNullOrWhiteSpace(NewTaskId));
        CancelCommand = new RelayCommand(() => Confirmed = false);
    }

    public async Task LoadAsync(CancellationToken ct = default)
        => await RefreshAsync(ct);

    public async Task<bool> ConfirmCreateAsync(CancellationToken ct = default)
    {
        await CreateAsync(ct);
        return Confirmed;
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            var list = await _blueprintService.ListAsync(_scope, ct);
            Blueprints.Clear();
            foreach (var item in list)
                Blueprints.Add(item);

            if (Blueprints.Count == 0)
            {
                SelectedBlueprint = null;
                StatusMessage = L("Task.BlueprintListEmpty");
                return;
            }

            var keepId = SelectedBlueprint?.BlueprintId;
            SelectedBlueprint = Blueprints.FirstOrDefault(x => string.Equals(x.BlueprintId, keepId, StringComparison.OrdinalIgnoreCase))
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

    private async Task DeleteSelectedAsync(CancellationToken ct)
    {
        if (SelectedBlueprint == null) return;

        var confirm = MessageBox.Show(
            string.Format(L("Task.BlueprintDeleteConfirm"), SelectedBlueprint.Name),
            L("Task.BlueprintDeleteTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
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

    private async Task CreateAsync(CancellationToken ct)
    {
        if (SelectedBlueprint == null)
        {
            StatusMessage = L("Task.BlueprintSelectRequired");
            return;
        }

        if (string.IsNullOrWhiteSpace(NewTaskId))
        {
            StatusMessage = L("Task.RequireTaskId");
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

            NewTaskId = NewTaskId.Trim();
            SelectedBlueprintRecord = blueprint;
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

    private static string L(string key)
        => Application.Current?.TryFindResource(key) as string ?? key;
}
