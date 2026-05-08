namespace SlurmJobManager.App.Services.Packaging;

public sealed record PackagingFeatureAuthorizationResult(
    bool IsAuthorized,
    string Reason);
