namespace SlurmPilot.App.Services.Updates;

public sealed record UpdateLaunchResult(
    bool IsSuccess,
    string? ErrorMessage,
    string? UpdaterPath);
