namespace SlurmPilot.App.Services.ExternalTargets;

public interface IExternalTargetOpener
{
    bool TryOpen(string pathOrUrl, out string? errorMessage);
}
