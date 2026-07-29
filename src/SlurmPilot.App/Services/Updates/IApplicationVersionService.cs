namespace SlurmPilot.App.Services.Updates;

public interface IApplicationVersionService
{
    Version CurrentVersion { get; }
    string CurrentVersionDisplay { get; }
}
