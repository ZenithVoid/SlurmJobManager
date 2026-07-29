using System.Windows;
using SlurmPilot.Core.Models;
using SlurmPilot.Core.Services;

namespace SlurmPilot.Updater;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!UpdaterLaunchContract.TryParse(args, out var request, out var parseError))
        {
            var fallbackLogger = UpdaterLogger.Create(null);
            fallbackLogger.Error($"Failed to parse launch arguments. {parseError}");
            ShowFailure($"无法启动更新器：{parseError}", fallbackLogger.LogPath);
            return 1;
        }

        using var logger = UpdaterLogger.Create(request!.LogFilePath);
        var updaterVersion = ApplicationVersionInfo.Resolve(typeof(Program).Assembly);
        logger.Info(
            $"Updater started. UpdaterVersion={updaterVersion.DisplayVersion}, CurrentAppVersion={request.CurrentVersionDisplay ?? "-"}, TargetVersion={request.TargetVersionDisplay ?? "-"}, ParentPid={request.ParentProcessId}, PackageType={request.PackageType}, PackagePath={request.UpdatePackagePath}");

        try
        {
            var runner = new UpdaterRunner(logger);
            runner.RunAsync(request).GetAwaiter().GetResult();
            logger.Info("Updater completed successfully.");
            return 0;
        }
        catch (Exception ex)
        {
            logger.Error("Updater failed.", ex);
            ShowFailure($"更新失败：{ex.Message}", logger.LogPath);
            return 1;
        }
    }

    private static void ShowFailure(string message, string logPath)
    {
        MessageBox.Show(
            $"{message}{Environment.NewLine}{Environment.NewLine}日志：{logPath}",
            "SlurmPilot Updater",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
