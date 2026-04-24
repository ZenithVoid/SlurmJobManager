using System.Windows;
using SlurmJobManager.App.ViewModels;
using SlurmJobManager.Infrastructure.Logs;
using SlurmJobManager.Infrastructure.Ssh;
using SlurmJobManager.Infrastructure.Storage;

namespace SlurmJobManager.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Infrastructure services (one SSH client shared across all consumers)
        var ssh      = new SshClientService();
        var slurm    = new SlurmService(ssh);
        var storage  = new TaskStorageService();
        var logChunk = new SshLogChunkService(ssh);

        // ViewModels
        var connectionVm = new ConnectionViewModel(ssh);
        var taskEditorVm = new TaskEditorViewModel(ssh, slurm, storage);
        var monitorVm    = new MonitorViewModel(slurm);
        var logViewerVm  = new LogViewerViewModel(logChunk);
        var consoleVm    = new ConsoleViewModel(ssh);

        var mainVm = new MainViewModel(connectionVm, taskEditorVm, monitorVm, logViewerVm, consoleVm);

        var mainWindow = new MainWindow { DataContext = mainVm };
        mainWindow.Show();
    }
}
