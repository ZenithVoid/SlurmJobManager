namespace SlurmJobManager.App.Services.Packaging;

public interface IPackagingFeatureAuthorizationService
{
    PackagingFeatureAuthorizationResult EvaluateAuthorization();
}
