namespace SlurmPilot.App.Services.Packaging;

public interface IPackagingFeatureAuthorizationService
{
    PackagingFeatureAuthorizationResult EvaluateAuthorization();
}
