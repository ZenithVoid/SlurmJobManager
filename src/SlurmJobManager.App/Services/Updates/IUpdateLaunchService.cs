using SlurmJobManager.Core.Models;

namespace SlurmJobManager.App.Services.Updates;

public interface IUpdateLaunchService
{
    bool TryCreateLaunchRequest(
        string packagePath,
        bool restartMainApplication,
        string? restartArguments,
        out UpdaterLaunchRequest? request,
        out string? errorMessage);

    UpdateLaunchResult LaunchUpdater(UpdaterLaunchRequest request);
}
