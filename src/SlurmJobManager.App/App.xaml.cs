using System.Windows;
using SlurmJobManager.App.Services;
using SlurmJobManager.App.ViewModels;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Models;
using SlurmJobManager.Infrastructure.Logs;
using SlurmJobManager.Infrastructure.Security;
using SlurmJobManager.Infrastructure.Ssh;
using SlurmJobManager.Infrastructure.Storage;

namespace SlurmJobManager.App;

public partial class App : Application
{
    private MainViewModel?      _mainVm;
    private SerilogAppLogger?   _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Initialize toast service (must be first so VMs can use it)
        ToastService.Initialize();

        // Shared application settings (timeouts / retry)
        var settings = new AppSettings();

        // Logging (must be initialised first so all services can use it)
        _logger = new SerilogAppLogger();
        _logger.Info("SlurmJobManager starting up.");

        // Infrastructure services (one SSH client shared across all consumers)
        var ssh      = new SshClientService(settings);
        var slurm    = new SlurmService(ssh, settings, _logger);
        var storage  = new TaskStorageService();
        var logChunk = new SshLogChunkService(ssh, settings, _logger);

        // Credential protection (DPAPI, Windows-only)
        IConnectionProfileStore? profileStore = null;
        if (DpapiCredentialProtector.IsSupported)
        {
            var protector = new DpapiCredentialProtector();
            profileStore  = new ConnectionProfileStore(protector, _logger);
        }

        // ViewModels
        var connectionVm = new ConnectionViewModel(ssh, profileStore);
        var taskEditorVm = new TaskEditorViewModel(ssh, slurm, storage);
        var monitorVm    = new MonitorViewModel(slurm, settings, _logger, connectionVm);
        var logViewerVm  = new LogViewerViewModel(logChunk, _logger);
        var consoleVm    = new ConsoleViewModel(ssh, _logger, connectionVm);

        // Wire SSH connection → TaskEditor auto-fill
        connectionVm.ConnectionEstablished += username => taskEditorVm.OnConnectionEstablished(username);

        _mainVm = new MainViewModel(connectionVm, taskEditorVm, monitorVm, logViewerVm, consoleVm);

        // Default locale: zh-CN (loaded regardless of system language)
        _mainVm.ApplyLocale("zh-CN");

        var mainWindow = new MainWindow { DataContext = _mainVm };
        mainWindow.Closing += OnMainWindowClosing;
        mainWindow.Show();
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Gracefully stop background tasks before the process exits
        if (_mainVm?.Monitor is MonitorViewModel monitor)
        {
            if (monitor.IsPolling)
            {
                _logger?.Info("Application closing — stopping monitor polling.");
                monitor.StopPollingCommand.Execute(null);
            }
            monitor.Dispose();
        }

        if (_mainVm?.LogViewer is LogViewerViewModel logViewer)
        {
            logViewer.Dispose();
        }

        if (_mainVm?.Console is ConsoleViewModel console)
        {
            console.Dispose();
        }

        _logger?.Info("SlurmJobManager shut down.");
        _logger?.Dispose();
    }
}
