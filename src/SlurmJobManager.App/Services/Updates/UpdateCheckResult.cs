namespace SlurmJobManager.App.Services.Updates;

public sealed record UpdateCheckResult(
    bool IsSuccess,
    UpdateSourceType SourceType,
    Version CurrentVersion,
    string CurrentVersionDisplay,
    Version? LatestVersion,
    string? LatestVersionDisplay,
    string? ReleaseTitle,
    DateTimeOffset? PublishedAt,
    string? ReleaseNotes,
    string? OpenTarget,
    string? ErrorMessage)
{
    public bool HasUpdate => IsSuccess && LatestVersion is not null && LatestVersion > CurrentVersion;
}
