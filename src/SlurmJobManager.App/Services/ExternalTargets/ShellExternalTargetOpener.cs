using System.Diagnostics;

namespace SlurmJobManager.App.Services.ExternalTargets;

public sealed class ShellExternalTargetOpener : IExternalTargetOpener
{
    public bool TryOpen(string pathOrUrl, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(pathOrUrl))
        {
            errorMessage = "Target is empty.";
            return false;
        }

        try
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = pathOrUrl,
                UseShellExecute = true,
            });

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }
}
