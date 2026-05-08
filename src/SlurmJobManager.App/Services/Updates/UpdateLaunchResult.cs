namespace SlurmJobManager.App.Services.Updates;

public sealed record UpdateLaunchResult(
    bool IsSuccess,
    string? ErrorMessage,
    string? UpdaterPath);
