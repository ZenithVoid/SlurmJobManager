namespace SlurmJobManager.App.Services.Updates;

public sealed record UpdateCheckRequest(
    UpdateSourceType SourceType,
    bool IncludePrerelease,
    string? FolderPath);
