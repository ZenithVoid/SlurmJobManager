namespace SlurmPilot.App.Services.Updates;

public sealed record UpdateConnectivityTestResult(
    bool IsSuccess,
    UpdateSourceType SourceType,
    string Target,
    string EffectiveProxyPolicy,
    bool UseProxyForUpdates,
    long DurationMs,
    string Summary,
    string? ErrorSummary,
    string? Suggestion);
