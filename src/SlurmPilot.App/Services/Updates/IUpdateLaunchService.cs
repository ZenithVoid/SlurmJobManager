using SlurmPilot.Core.Models;

namespace SlurmPilot.App.Services.Updates;

public interface IUpdateLaunchService
{
    bool TryCreateLaunchRequest(
        string packagePath,
        bool restartMainApplication,
        string? restartArguments,
        string? targetVersionDisplay,
        out UpdaterLaunchRequest? request,
        out string? errorMessage);

    UpdateLaunchResult LaunchUpdater(UpdaterLaunchRequest request);
}
