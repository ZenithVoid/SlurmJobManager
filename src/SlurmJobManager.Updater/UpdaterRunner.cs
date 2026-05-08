using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using SlurmJobManager.Core.Models;

namespace SlurmJobManager.Updater;

internal sealed class UpdaterRunner(UpdaterLogger logger)
{
    private static readonly TimeSpan MainProcessExitTimeout = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan InstallerWaitTimeout = TimeSpan.FromMinutes(30);

    private readonly UpdaterLogger _logger = logger;

    public async Task RunAsync(UpdaterLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        await EnsureMainProcessStoppedAsync(request.ParentProcessId);

        switch (request.PackageType)
        {
            case UpdatePackageType.Zip:
                await ApplyZipUpdateAsync(request);
                break;
            case UpdatePackageType.Installer:
                await RunInstallerUpdateAsync(request);
                break;
            default:
                throw new InvalidOperationException($"Unsupported package type: {request.PackageType}");
        }

        if (request.RestartMainApplication)
            RestartMainApplication(request);
    }

    private static void ValidateRequest(UpdaterLaunchRequest request)
    {
        if (!File.Exists(request.UpdatePackagePath))
            throw new FileNotFoundException("Update package does not exist.", request.UpdatePackagePath);

        if (!Directory.Exists(request.InstallDirectory))
            throw new DirectoryNotFoundException($"Install directory does not exist: {request.InstallDirectory}");
    }

    private async Task EnsureMainProcessStoppedAsync(int processId)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            _logger.Info($"Main process PID {processId} is not running.");
            return;
        }

        using (process)
        {
            if (process.HasExited)
            {
                _logger.Info($"Main process PID {processId} has already exited.");
                return;
            }

            _logger.Info($"Waiting for main process PID {processId} to exit.");
            if (await WaitForExitAsync(process, MainProcessExitTimeout))
            {
                _logger.Info("Main process exited naturally.");
                return;
            }

            _logger.Warn("Main process did not exit in time; attempting graceful close.");
            TryCloseMainWindow(process);
            if (await WaitForExitAsync(process, TimeSpan.FromSeconds(8)))
            {
                _logger.Info("Main process exited after close request.");
                return;
            }

            _logger.Warn("Main process is still running; forcing process termination.");
            process.Kill(entireProcessTree: true);
            if (await WaitForExitAsync(process, TimeSpan.FromSeconds(8)))
            {
                _logger.Info("Main process terminated successfully.");
                return;
            }

            throw new TimeoutException("Main process could not be stopped before update.");
        }
    }

    private void TryCloseMainWindow(Process process)
    {
        try
        {
            _ = process.CloseMainWindow();
        }
        catch (Exception ex)
        {
            _logger.Warn($"CloseMainWindow failed: {ex.Message}");
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        if (process.HasExited) return true;
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }
    }

    private async Task ApplyZipUpdateAsync(UpdaterLaunchRequest request)
    {
        _logger.Info("Starting zip update.");
        var stagingRoot = Path.Combine(Path.GetTempPath(), "SlurmJobManagerUpdater", Guid.NewGuid().ToString("N"));
        var extractedDir = Path.Combine(stagingRoot, "extracted");
        var backupRoot = Path.Combine(request.InstallDirectory, ".updater-backup", DateTime.Now.ToString("yyyyMMddHHmmss"));
        var createdFiles = new List<string>();
        var overwrittenFiles = new List<(string Destination, string Backup)>();

        try
        {
            Directory.CreateDirectory(extractedDir);
            Directory.CreateDirectory(backupRoot);
            _logger.Info($"Extracting package to temporary directory: {extractedDir}");
            ZipFile.ExtractToDirectory(request.UpdatePackagePath, extractedDir, overwriteFiles: true);

            var mainExeName = Path.GetFileName(request.MainExecutablePath);
            var extractedMainExe = Path.Combine(extractedDir, mainExeName);
            if (!File.Exists(extractedMainExe))
                throw new InvalidOperationException($"Zip package is missing required file: {mainExeName}");

            var updaterExePath = Environment.ProcessPath;
            foreach (var sourceFile in Directory.EnumerateFiles(extractedDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(extractedDir, sourceFile);
                var destinationPath = Path.Combine(request.InstallDirectory, relativePath);

                if (!string.IsNullOrWhiteSpace(updaterExePath) &&
                    string.Equals(Path.GetFullPath(destinationPath), Path.GetFullPath(updaterExePath), StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Warn($"Skipped replacing running updater executable: {destinationPath}");
                    continue;
                }

                EnsureParentDirectory(destinationPath);

                if (File.Exists(destinationPath))
                {
                    var backupPath = Path.Combine(backupRoot, relativePath);
                    EnsureParentDirectory(backupPath);
                    File.Copy(destinationPath, backupPath, overwrite: true);
                    overwrittenFiles.Add((destinationPath, backupPath));
                }
                else
                {
                    createdFiles.Add(destinationPath);
                }

                File.Copy(sourceFile, destinationPath, overwrite: true);
            }

            if (!File.Exists(request.MainExecutablePath))
                throw new InvalidOperationException($"Main executable missing after zip update: {request.MainExecutablePath}");

            _logger.Info("Zip update completed successfully.");
        }
        catch
        {
            _logger.Warn("Zip update failed. Attempting rollback.");
            Rollback(createdFiles, overwrittenFiles);
            throw;
        }
        finally
        {
            await CleanupDirectoryAsync(stagingRoot);
        }
    }

    private void Rollback(IEnumerable<string> createdFiles, IEnumerable<(string Destination, string Backup)> overwrittenFiles)
    {
        foreach (var file in createdFiles)
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to remove created file during rollback: {file}. {ex.Message}");
            }
        }

        foreach (var (destination, backup) in overwrittenFiles)
        {
            try
            {
                if (File.Exists(backup))
                    File.Copy(backup, destination, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to restore backup during rollback: {destination}. {ex.Message}");
            }
        }
    }

    private async Task RunInstallerUpdateAsync(UpdaterLaunchRequest request)
    {
        _logger.Info("Starting installer update.");
        var startInfo = new ProcessStartInfo
        {
            FileName = request.UpdatePackagePath,
            WorkingDirectory = Path.GetDirectoryName(request.UpdatePackagePath) ?? request.InstallDirectory,
            UseShellExecute = true,
        };

        using var process = Process.Start(startInfo);
        if (process is null)
            throw new InvalidOperationException("Failed to start installer process.");

        _logger.Info($"Installer process started. PID={process.Id}");
        if (!await WaitForExitAsync(process, InstallerWaitTimeout))
            throw new TimeoutException("Installer did not finish within the allowed timeout.");

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Installer exited with code {process.ExitCode}.");

        _logger.Info("Installer update completed successfully.");
    }

    private void RestartMainApplication(UpdaterLaunchRequest request)
    {
        _logger.Info("Restarting main application.");
        var mainExecutablePath = File.Exists(request.MainExecutablePath)
            ? request.MainExecutablePath
            : Path.Combine(request.InstallDirectory, Path.GetFileName(request.MainExecutablePath));

        if (!File.Exists(mainExecutablePath))
            throw new FileNotFoundException("Main executable was not found after update.", mainExecutablePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = mainExecutablePath,
            WorkingDirectory = request.InstallDirectory,
            UseShellExecute = true,
            Arguments = request.RestartArguments ?? string.Empty,
        };

        var process = Process.Start(startInfo);
        if (process is null)
            throw new InvalidOperationException("Failed to restart main application.");

        _logger.Info($"Main application restarted. PID={process.Id}");
    }

    private async Task CleanupDirectoryAsync(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            await Task.Run(() => Directory.Delete(path, recursive: true));
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to clean temporary directory: {path}. {ex.Message}");
        }
    }

    private static void EnsureParentDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException($"Cannot resolve parent directory for path: {filePath}");
        Directory.CreateDirectory(directory);
    }
}
