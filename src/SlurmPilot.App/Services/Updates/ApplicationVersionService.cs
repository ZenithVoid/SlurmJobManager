using SlurmPilot.Core.Services;

namespace SlurmPilot.App.Services.Updates;

public sealed class ApplicationVersionService : IApplicationVersionService
{
    public Version CurrentVersion { get; }
    public string CurrentVersionDisplay { get; }

    public ApplicationVersionService()
    {
        var info = ApplicationVersionInfo.Resolve();
        CurrentVersion = info.ComparableVersion;
        CurrentVersionDisplay = info.DisplayVersion;
    }
}
