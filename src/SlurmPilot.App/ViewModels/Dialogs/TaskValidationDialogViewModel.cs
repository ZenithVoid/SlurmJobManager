using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using SlurmPilot.App.Services.Validation;

namespace SlurmPilot.App.ViewModels.Dialogs;

public sealed class TaskValidationDialogViewModel : ViewModelBase
{
    private readonly ITaskValidationService _validationService;
    private readonly Func<TaskValidationContext> _contextFactory;
    private readonly Func<TaskValidationIssue, CancellationToken, Task<string?>> _quickFixExecutor;
    private readonly TaskValidationOperation _operation;
    private bool _isBusy;
    private string _statusMessage = string.Empty;
    private string _statusStyleKey = "InfoTextStyle";

    public TaskValidationDialogViewModel(
        ITaskValidationService validationService,
        Func<TaskValidationContext> contextFactory,
        Func<TaskValidationIssue, CancellationToken, Task<string?>> quickFixExecutor,
        TaskValidationOperation operation)
    {
        _validationService = validationService;
        _contextFactory = contextFactory;
        _quickFixExecutor = quickFixExecutor;
        _operation = operation;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        ApplyFixCommand = new AsyncRelayCommand<TaskValidationIssueItemViewModel>(ApplyFixAsync, item => !IsBusy && item?.CanQuickFix == true);
        ApplyAllFixesCommand = new AsyncRelayCommand(ApplyAllFixesAsync, () => !IsBusy && Issues.Any(static i => i.CanQuickFix));
    }

    public ObservableCollection<TaskValidationIssueItemViewModel> Issues { get; } = new();
    public ICommand RefreshCommand { get; }
    public ICommand ApplyFixCommand { get; }
    public ICommand ApplyAllFixesCommand { get; }

    public bool ContinueRequested { get; set; }

    public string Title => _operation == TaskValidationOperation.Submit
        ? L("Task.Validation.SubmitDialogTitle")
        : L("Task.Validation.SaveDialogTitle");

    public string ContinueButtonText => _operation == TaskValidationOperation.Submit
        ? L("Task.Validation.ContinueSubmit")
        : L("Task.Validation.ContinueSave");

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasAnyIssue => Issues.Count > 0;
    public bool HasBlockingIssues => Issues.Any(static i => i.IsBlocking);
    public bool CanContinue => !HasBlockingIssues;
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public string StatusStyleKey
    {
        get => _statusStyleKey;
        private set => SetField(ref _statusStyleKey, value);
    }

    public string SummaryText
    {
        get
        {
            if (!HasAnyIssue)
                return L("Task.Validation.SummaryNoIssue");

            var blockingCount = Issues.Count(static i => i.IsBlocking);
            var warningCount = Issues.Count(static i => i.Source.Severity == TaskValidationSeverity.Warning);
            var infoCount = Issues.Count(static i => i.Source.Severity == TaskValidationSeverity.Info);
            return string.Format(L("Task.Validation.SummaryWithIssue"), blockingCount, warningCount, infoCount);
        }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            var result = await _validationService.ValidateAsync(_contextFactory(), _operation, ct);
            RebuildIssues(result.Issues);

            if (HasBlockingIssues)
            {
                StatusStyleKey = "WarningTextStyle";
                StatusMessage = L("Task.Validation.StatusBlocking");
            }
            else if (HasAnyIssue)
            {
                StatusStyleKey = "InfoTextStyle";
                StatusMessage = L("Task.Validation.StatusNonBlocking");
            }
            else
            {
                StatusStyleKey = "SuccessTextStyle";
                StatusMessage = L("Task.Validation.StatusReady");
            }
        }
        catch (Exception ex)
        {
            StatusStyleKey = "ErrorTextStyle";
            StatusMessage = string.Format(L("Task.Validation.StatusRefreshFailed"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ApplyFixAsync(TaskValidationIssueItemViewModel? item, CancellationToken ct)
    {
        if (item == null || !item.CanQuickFix || IsBusy)
            return;

        IsBusy = true;
        try
        {
            var fixFeedback = await _quickFixExecutor(item.Source, ct);
            if (!string.IsNullOrWhiteSpace(fixFeedback))
            {
                StatusStyleKey = "InfoTextStyle";
                StatusMessage = fixFeedback;
            }
        }
        catch (Exception ex)
        {
            StatusStyleKey = "ErrorTextStyle";
            StatusMessage = string.Format(L("Task.Validation.FixFailed"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync(ct);
    }

    private async Task ApplyAllFixesAsync(CancellationToken ct)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            foreach (var issue in Issues.Where(static i => i.CanQuickFix).Select(static i => i.Source).ToList())
                _ = await _quickFixExecutor(issue, ct);
        }
        catch (Exception ex)
        {
            StatusStyleKey = "ErrorTextStyle";
            StatusMessage = string.Format(L("Task.Validation.FixFailed"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync(ct);
    }

    private void RebuildIssues(IReadOnlyList<TaskValidationIssue> issues)
    {
        Issues.Clear();
        foreach (var issue in issues)
            Issues.Add(new TaskValidationIssueItemViewModel(issue));

        OnPropertyChanged(nameof(HasAnyIssue));
        OnPropertyChanged(nameof(HasBlockingIssues));
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(SummaryText));
        CommandManager.InvalidateRequerySuggested();
    }

    private static string L(string key)
        => Application.Current?.TryFindResource(key) as string ?? key;
}

public sealed class TaskValidationIssueItemViewModel
{
    public TaskValidationIssueItemViewModel(TaskValidationIssue source)
    {
        Source = source;
    }

    public TaskValidationIssue Source { get; }
    public string Title => Source.Title;
    public string Description => Source.Description;
    public bool IsBlocking => Source.IsBlocking;
    public bool CanQuickFix => Source.QuickFixKind != TaskValidationQuickFixKind.None;
    public string QuickFixText => Source.QuickFixText;
    public string SeverityText => Source.Severity switch
    {
        TaskValidationSeverity.Error => L("Task.Validation.SeverityError"),
        TaskValidationSeverity.Warning => L("Task.Validation.SeverityWarning"),
        _ => L("Task.Validation.SeverityInfo"),
    };

    public string SeverityStyleKey => Source.Severity switch
    {
        TaskValidationSeverity.Error => "ErrorTextStyle",
        TaskValidationSeverity.Warning => "WarningTextStyle",
        _ => "InfoTextStyle",
    };

    private static string L(string key)
        => Application.Current?.TryFindResource(key) as string ?? key;
}
