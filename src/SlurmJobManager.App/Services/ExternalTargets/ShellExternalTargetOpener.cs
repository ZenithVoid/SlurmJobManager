using System.Diagnostics;
using System.IO;

namespace SlurmJobManager.App.Services.ExternalTargets;

public sealed class ShellExternalTargetOpener : IExternalTargetOpener
{
    public bool TryOpen(string pathOrUrl, out string? errorMessage)
    {
        errorMessage = null;
        var target = pathOrUrl?.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            errorMessage = "Target is empty.";
            return false;
        }

        target = target.Trim('"');

        try
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            });

            return true;
        }
        catch (Exception ex)
        {
            if (TryOpenWithExplorerFallback(target))
                return true;

            errorMessage = ex.Message;
            return false;
        }
    }

    private static bool TryOpenWithExplorerFallback(string target)
    {
        if (!Directory.Exists(target) && !File.Exists(target))
            return false;

        try
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{Path.GetFullPath(target)}\"",
                UseShellExecute = false,
            });

            return true;
        }
        catch
        {
            return false;
        }
    }
}
