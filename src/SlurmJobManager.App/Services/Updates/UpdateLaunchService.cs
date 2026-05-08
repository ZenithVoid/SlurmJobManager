using System.Diagnostics;
using System.IO;
using SlurmJobManager.Core.Models;
using SlurmJobManager.Core.Services;

namespace SlurmJobManager.App.Services.Updates;

public sealed class UpdateLaunchService : IUpdateLaunchService
{
    private static readonly string[] InstallerExtensions = [".exe", ".msi"];

    public bool TryCreateLaunchRequest(
        string packagePath,
        bool restartMainApplication,
        string? restartArguments,
        out UpdaterLaunchRequest? request,
        out string? errorMessage)
    {
        request = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(packagePath))
        {
            errorMessage = "Update package path is empty.";
            return false;
        }

        if (!File.Exists(packagePath))
        {
            errorMessage = $"Update package was not found: {packagePath}";
            return false;
        }

        if (!TryResolvePackageType(packagePath, out var packageType))
        {
            errorMessage = "Unsupported update package type. Only .zip, .exe and .msi are supported.";
            return false;
        }

        var mainExecutablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(mainExecutablePath))
        {
            errorMessage = "Cannot resolve main executable path.";
            return false;
        }

        var installDirectory = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            errorMessage = "Cannot resolve install directory.";
            return false;
        }

        var logFilePath = LocalDataPaths.UpdaterLogFilePath;
        request = new UpdaterLaunchRequest(
            ParentProcessId: Environment.ProcessId,
            MainExecutablePath: Path.GetFullPath(mainExecutablePath),
            InstallDirectory: Path.GetFullPath(installDirectory),
            UpdatePackagePath: Path.GetFullPath(packagePath),
            PackageType: packageType,
            RestartMainApplication: restartMainApplication,
            RestartArguments: restartArguments,
            LogFilePath: logFilePath);
        return true;
    }

    public UpdateLaunchResult LaunchUpdater(UpdaterLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var updaterPath = ResolveUpdaterPath();
        if (updaterPath is null)
            return new UpdateLaunchResult(false, "Updater executable was not found.", null);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = updaterPath,
                Arguments = UpdaterLaunchContract.BuildCommandLineArguments(request),
                WorkingDirectory = Path.GetDirectoryName(updaterPath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
            };

            var process = Process.Start(startInfo);
            if (process is null)
                return new UpdateLaunchResult(false, "Failed to start updater process.", updaterPath);

            return new UpdateLaunchResult(true, null, updaterPath);
        }
        catch (Exception ex)
        {
            return new UpdateLaunchResult(false, $"Failed to launch updater: {ex.Message}", updaterPath);
        }
    }

    private static bool TryResolvePackageType(string packagePath, out UpdatePackageType packageType)
    {
        var extension = Path.GetExtension(packagePath);
        if (string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
        {
            packageType = UpdatePackageType.Zip;
            return true;
        }

        if (InstallerExtensions.Any(ext => string.Equals(ext, extension, StringComparison.OrdinalIgnoreCase)))
        {
            packageType = UpdatePackageType.Installer;
            return true;
        }

        packageType = default;
        return false;
    }

    private static string? ResolveUpdaterPath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "Updater", "SlurmJobManager.Updater.exe"),
            Path.Combine(baseDirectory, "SlurmJobManager.Updater.exe"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
