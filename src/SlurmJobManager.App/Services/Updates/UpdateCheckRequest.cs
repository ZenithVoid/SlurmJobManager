namespace SlurmJobManager.App.Services.Updates;

public sealed record UpdateCheckRequest(
    UpdateSourceType SourceType,
    bool IncludePrerelease,
    string? FolderPath,
    bool UseProxyForUpdates,
    UpdateProxyMode ProxyMode,
    string? CustomProxyHost,
    int? CustomProxyPort);
