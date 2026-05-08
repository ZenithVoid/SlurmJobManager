namespace SlurmJobManager.App.Services.Validation;

public interface ITaskValidationService
{
    Task<TaskValidationResult> ValidateAsync(
        TaskValidationContext context,
        TaskValidationOperation operation,
        CancellationToken ct = default);
}
